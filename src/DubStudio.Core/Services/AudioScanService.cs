using DubStudio.Core.Data;
using DubStudio.Core.Media;
using DubStudio.Core.Models;

namespace DubStudio.Core.Services;

public enum AudioIssue { MissingFile, HashChanged, SizeChanged, Rejected, VersionMismatch, PendingPick }

public sealed record AudioIssueItem(int TakeId, int LineId, int TakeNo, AudioIssue Issue, string Detail, string? Path);

public sealed record AudioScanReport(int TotalTakes, int Missing, int Changed, int Other, List<AudioIssueItem> Items)
{
    public bool Clean => Missing == 0 && Changed == 0;
}

/// <summary>交付音频缺失/被外部改动扫描。比对条次记录的 SHA-256 与大小，
/// 并把外部改动推入复核队列（旧录音不自动适用）。</summary>
public sealed class AudioScanService
{
    private readonly StudioDatabase _db;
    public AudioScanService(StudioDatabase db) => _db = db;

    public async Task<AudioScanReport> ScanAsync(int projectId = 1, bool queueReviews = true, CancellationToken ct = default)
    {
        var lines = _db.Conn.Table<ScriptLine>().Where(l => l.ProjectId == projectId).ToList();
        var lineIds = lines.Select(l => l.Id).ToHashSet();
        // 数据量为棚内规模，全部载入内存分组，规避不同 SQLite 提供程序对 IN 列表的翻译差异
        var takes = _db.Conn.Table<Take>().ToList().Where(t => lineIds.Contains(t.LineId)).ToList();
        var linesById = lines.ToDictionary(l => l.Id);
        var picks = _db.Conn.Table<DirectorPick>().ToList().Where(p => lineIds.Contains(p.LineId)).ToList();

        var items = new List<AudioIssueItem>();
        int missing = 0, changed = 0, other = 0;

        foreach (var take in takes)
        {
            void Add(AudioIssue issue, string detail)
            {
                items.Add(new AudioIssueItem(take.Id, take.LineId, take.TakeNo, issue, detail, take.AudioFilePath));
                if (issue is AudioIssue.MissingFile) missing++;
                else if (issue is AudioIssue.HashChanged or AudioIssue.SizeChanged) changed++;
                else other++;
            }

            if (string.IsNullOrWhiteSpace(take.AudioFilePath))
            {
                Add(AudioIssue.MissingFile, "条次未登记音频文件");
                continue;
            }
            if (!File.Exists(take.AudioFilePath))
            {
                Add(AudioIssue.MissingFile, "文件不存在: " + take.AudioFilePath);
                if (queueReviews)
                {
                    take.Status = TakeStatus.NeedsReview;
                    _db.Conn.Update(take);
                    ReviewWorkflow.QueueReview(_db.Conn, take.LineId, take.Id,
                        ReviewKind.ExternalFileChanged, "交付音频缺失: " + take.AudioFilePath);
                }
                continue;
            }

            var info = new FileInfo(take.AudioFilePath);
            bool sizeChanged = take.FileSizeBytes > 0 && info.Length != take.FileSizeBytes;

            var hash = await FileHasher.Sha256Async(take.AudioFilePath!, ct).ConfigureAwait(false);
            var hashChanged = !string.IsNullOrEmpty(take.FileSha256) &&
                !string.Equals(take.FileSha256, hash, StringComparison.OrdinalIgnoreCase);
            if (hashChanged)
            {
                Add(AudioIssue.HashChanged, "文件被外部程序改动（SHA-256 不一致）");
                if (queueReviews)
                {
                    if (take.Status == TakeStatus.Usable)
                    {
                        take.Status = TakeStatus.NeedsReview;
                        _db.Conn.Update(take);
                    }
                    // 保留录制时的原始摘要/大小作为基线，不覆盖：
                    // 归档需持续识别“交付文件已偏离录音定稿”，直到录音师重新登记条次。
                    _db.Conn.Update(take);
                    ReviewWorkflow.QueueReview(_db.Conn, take.LineId, take.Id,
                        ReviewKind.ExternalFileChanged, "音频文件在外部被修改，需复核后采用");
                    var pick = picks.FirstOrDefault(p => p.TakeId == take.Id);
                    if (pick != null && pick.Status == PickStatus.Confirmed)
                    {
                        pick.Status = PickStatus.PendingReview;
                        pick.UpdatedAt = ReviewWorkflow.Now();
                        _db.Conn.Update(pick);
                    }
                }
                continue;
            }
            else if (sizeChanged)
            {
                Add(AudioIssue.SizeChanged,
                    $"文件大小变化: 记录 {take.FileSizeBytes} / 实际 {info.Length}");
            }

            if (take.Status == TakeStatus.Rejected)
                Add(AudioIssue.Rejected, "条次已否决");
            else if (linesById.TryGetValue(take.LineId, out var line) && take.TextVersionId != line.CurrentVersionId)
                Add(AudioIssue.VersionMismatch, "条次基于旧译文版本");
        }

        foreach (var pick in picks.Where(p => p.Status == PickStatus.PendingReview))
        {
            if (!items.Any(i => i.TakeId == pick.TakeId))
            {
                items.Add(new AudioIssueItem(pick.TakeId, pick.LineId, 0, AudioIssue.PendingPick,
                    "导演选用处于待复核", null));
                other++;
            }
        }

        return new AudioScanReport(takes.Count, missing, changed, other,
            items.OrderBy(i => i.LineId).ThenBy(i => i.TakeNo).ToList());
    }
}
