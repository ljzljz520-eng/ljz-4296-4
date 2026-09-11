using System.Text;
using System.Text.Json;
using DubStudio.Core.Data;
using DubStudio.Core.Media;
using DubStudio.Core.Models;

namespace DubStudio.Core.Services;

public sealed record DeliveredFile(
    string RelativePath,
    int LineId,
    int TakeId,
    string LineCode,
    LineDisposition Disposition,
    long Size,
    string? Sha256,
    bool CarriedOver,
    string? Actor,
    string? BlockedReason);

public sealed record ChangeListEntry(
    string LineCode,
    LineChangeKind Kind,
    LineDisposition Disposition,
    string KindLabel,
    string DispositionLabel,
    string Basis,
    string? OldText,
    string? NewText,
    string Note);

public sealed record RevisionDeliveryResult(
    int DeliveryId,
    string OutputFolder,
    int Attempt,
    int ExportedFiles,
    int CarriedOver,
    int CastBlocked,
    int RerecordPending,
    string ChangeListPath);

/// <summary>修订交付：已经交给混音的文件绝不原位替换——每次交付写入独立的新目录
/// （REV-n_vN/），复制当前有效音频，并附机器可读的变更清单 changelist.json
/// 与给混音组的 changelist.txt。换演员的历史录音一律挡在新批次之外。</summary>
public sealed class RevisionDeliveryService
{
    private readonly StudioDatabase _db;
    public RevisionDeliveryService(StudioDatabase db) => _db = db;

    public async Task<RevisionDeliveryResult> DeliverAsync(int packageId, string outputRoot,
        CancellationToken ct = default)
    {
        var pkg = _db.Conn.Find<RevisionPackage>(packageId)
            ?? throw new InvalidOperationException("修订包不存在");
        if (pkg.State is RevisionState.Draft or RevisionState.Withdrawn)
            throw new InvalidOperationException($"修订包 {pkg.Code} 未确认（{pkg.State}），不能交付");

        var attempt = (_db.Conn.Table<MixDelivery>()
            .Where(d => d.RevisionPackageId == packageId)
            .Select(d => d.Attempt).DefaultIfEmpty(0).Max()) + 1;
        var folder = Path.Combine(outputRoot, $"{pkg.Code}_v{attempt}");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "audio"));

        var changes = _db.Conn.Table<RevisionChange>().Where(c => c.PackageId == packageId).ToList();
        var lineCodes = _db.Conn.Table<ScriptLine>().ToDictionary(l => l.Id, l => l.Code);
        var cast = new CastService(_db);

        var files = new List<DeliveredFile>();
        var listEntries = new List<ChangeListEntry>();
        int carried = 0, blocked = 0, rerecordPending = 0;

        foreach (var ch in changes.OrderBy(c => c.Id))
        {
            var lineId = ch.Kind == LineChangeKind.Added || ch.Kind == LineChangeKind.Split
                ? (ch.TargetLineId ?? ch.LineId)
                : ch.LineId;
            var code = lineCodes.TryGetValue(lineId, out var c) ? c : $"#{lineId}";
            var oldText = ch.SnapshotTranslatedText;
            var newText = ch.NewTranslatedText ?? ch.NewOriginalText;
            listEntries.Add(new ChangeListEntry(code, ch.Kind, ch.Disposition,
                Label(ch.Kind), DispositionLabel(ch.Disposition),
                ch.DecisionBasis, oldText, newText, ch.SuggestionBasis));

            if (ch.Disposition is LineDisposition.Retire) continue;
            if (ch.Disposition is LineDisposition.NewRecording or LineDisposition.NeedsRerecord)
            {
                var take = CurrentPickTake(lineId);
                if (take == null)
                {
                    rerecordPending++;
                    continue;
                }
                // 确认后补录并已选用的新条次照常交付（此时演员为当前演员）
                var copy = await TryCopyAsync(files, folder, code, lineId, take, ch.Disposition, false, cast, ct)
                    .ConfigureAwait(false);
                if (copy == null) blocked++;
            }
            else if (ch.Disposition is LineDisposition.ReeditOnly or LineDisposition.Keep)
            {
                var take = CurrentPickTake(lineId) ?? NewestUsableTake(lineId);
                if (take == null) continue;
                var copy = await TryCopyAsync(files, folder, code, lineId, take, ch.Disposition,
                    ch.Disposition == LineDisposition.Keep, cast, ct).ConfigureAwait(false);
                if (copy == null) blocked++;
                else if (copy.CarriedOver) carried++;
            }
        }

        // 时间轴平移：包外未变的已选用句也带出当前选用音频，供混音整体对齐
        foreach (var lid in RevisionPackageService.ParseIds(pkg.ShiftLineIds))
        {
            var take = CurrentPickTake(lid) ?? NewestUsableTake(lid);
            if (take == null || files.Any(f => f.TakeId == take.Id)) continue;
            var code = lineCodes.TryGetValue(lid, out var c) ? c : $"#{lid}";
            var copy = await TryCopyAsync(files, folder, code, lid, take, LineDisposition.ReeditOnly, false, cast, ct)
                .ConfigureAwait(false);
            if (copy == null) blocked++;
        }

        WriteChangeList(folder, pkg, listEntries, files);
        var exported = files.Count(f => f.RelativePath != "");

        var delivery = new MixDelivery
        {
            ProjectId = pkg.ProjectId,
            RevisionPackageId = packageId,
            RevisionOrdinal = pkg.Ordinal,
            Attempt = attempt,
            OutputFolder = folder,
            DeliveredAt = ReviewWorkflow.Now(),
            ExportedFiles = exported,
            CastBlocked = blocked,
            ChangeListPath = Path.Combine(folder, "changelist.json")
        };
        _db.Conn.Insert(delivery);

        pkg.State = RevisionState.Delivered;
        pkg.DeliveredAt = delivery.DeliveredAt;
        _db.Conn.Update(pkg);

        return new RevisionDeliveryResult(delivery.Id, folder, attempt, exported,
            carried, blocked, rerecordPending, delivery.ChangeListPath);
    }

    private Take? CurrentPickTake(int lineId)
    {
        var pick = _db.Conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == lineId);
        return pick == null ? null : _db.Conn.Find<Take>(pick.TakeId);
    }

    private Take? NewestUsableTake(int lineId)
        => _db.Conn.Table<Take>().Where(t => t.LineId == lineId && t.Status == TakeStatus.Usable)
            .OrderByDescending(t => t.TakeNo).FirstOrDefault();

    private async Task<DeliveredFile?> TryCopyAsync(List<DeliveredFile> files, string folder,
        string lineCode, int lineId, Take take, LineDisposition disposition, bool carriedOver,
        CastService cast, CancellationToken ct)
    {
        if (!cast.IsCurrentActor(take))
        {
            var line = _db.Conn.Find<ScriptLine>(lineId);
            var current = line == null ? null : cast.GetCast(line.ProjectId, line.CharacterId)?.Actor;
            files.Add(new DeliveredFile("", lineId, take.Id, lineCode, disposition, 0, null, false,
                take.Actor,
                $"换演员：条次演员“{take.Actor}”≠当前演员“{current}”，历史录音保留但不混入新批次"));
            return null;
        }
        if (string.IsNullOrWhiteSpace(take.AudioFilePath) || !File.Exists(take.AudioFilePath))
        {
            files.Add(new DeliveredFile("", lineId, take.Id, lineCode, disposition, 0, null, false,
                take.Actor, "音频文件缺失，未导出"));
            return null;
        }

        var ext = Path.GetExtension(take.AudioFilePath).ToLowerInvariant();
        var rel = UniqueRel(files, lineCode, take, ext);
        var dest = Path.Combine(folder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using (var src = new FileStream(take.AudioFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        await using (var fs = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
            await src.CopyToAsync(fs, ct).ConfigureAwait(false);

        var info = new FileInfo(dest);
        var hash = await FileHasher.Sha256Async(dest, ct).ConfigureAwait(false);
        var entry = new DeliveredFile(rel, lineId, take.Id, lineCode, disposition, info.Length, hash,
            carriedOver, take.Actor, null);
        files.Add(entry);
        return entry;
    }

    private static string UniqueRel(List<DeliveredFile> files, string lineCode, Take take, string ext)
    {
        var safe = new string(lineCode.Select(ch => "/\\:*?\"<>|".IndexOf(ch) >= 0 ? '_' : ch).ToArray());
        var rel = $"audio/{safe}_take{take.TakeNo:00}{ext}";
        var n = 1;
        while (files.Any(f => f.RelativePath == rel))
            rel = $"audio/{safe}_take{take.TakeNo:00}_{n++}{ext}";
        return rel;
    }

    private void WriteChangeList(string folder, RevisionPackage pkg,
        List<ChangeListEntry> entries, List<DeliveredFile> files)
    {
        var doc = new
        {
            package = pkg.Code,
            reason = pkg.Reason,
            submittedBy = pkg.SubmittedBy,
            timelineShiftMs = pkg.TimelineShiftMs,
            generatedAt = ReviewWorkflow.Now(),
            changes = entries,
            exports = files.Where(f => f.RelativePath != ""),
            blocked = files.Where(f => f.BlockedReason != null)
                .Select(f => new { f.LineCode, f.TakeId, f.Actor, f.BlockedReason })
        };
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // 变更清单给中文混音/译制组直接阅读，不把汉字转义成 \uXXXX
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(Path.Combine(folder, "changelist.json"), json, Encoding.UTF8);

        var sb = new StringBuilder();
        sb.AppendLine($"修订包 {pkg.Code} 变更清单（{ReviewWorkflow.Now()}）");
        sb.AppendLine($"原因：{pkg.Reason}    提交：{pkg.SubmittedBy}    时间轴平移：{pkg.TimelineShiftMs:0}ms");
        sb.AppendLine(new string('-', 60));
        foreach (var e in entries)
        {
            sb.AppendLine($"[{e.LineCode}] {Label(e.Kind)} → {DispositionLabel(e.Disposition)}");
            if (!string.IsNullOrEmpty(e.OldText)) sb.AppendLine("  旧：" + e.OldText);
            if (!string.IsNullOrEmpty(e.NewText)) sb.AppendLine("  新：" + e.NewText);
            sb.AppendLine("  依据：" + e.Basis);
        }
        var blockList = files.Where(f => f.BlockedReason != null).ToList();
        if (blockList.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("以下历史条次未混入本批次：");
            foreach (var b in blockList) sb.AppendLine($"- {b.LineCode} 条{b.TakeId}：{b.BlockedReason}");
        }
        File.WriteAllText(Path.Combine(folder, "changelist.txt"), sb.ToString(), Encoding.UTF8);
    }

    internal static string Label(LineChangeKind k) => k switch
    {
        LineChangeKind.TextTweak => "文字微调（需补录）",
        LineChangeKind.DurationChanged => "时长变化（仅需重剪）",
        LineChangeKind.TextAndDuration => "文字+时长变化（需补录）",
        LineChangeKind.Deleted => "删句",
        LineChangeKind.Added => "新增句",
        LineChangeKind.Split => "语句拆分",
        LineChangeKind.Merged => "语句合并",
        _ => k.ToString()
    };

    internal static string DispositionLabel(LineDisposition d) => d switch
    {
        LineDisposition.NeedsRerecord => "需补录",
        LineDisposition.ReeditOnly => "仅需重剪",
        LineDisposition.Keep => "可以保留",
        LineDisposition.Retire => "退役删句",
        LineDisposition.NewRecording => "等待补录",
        _ => "未决定"
    };
}
