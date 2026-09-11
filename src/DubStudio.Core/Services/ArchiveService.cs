using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DubStudio.Core.Data;
using DubStudio.Core.Media;
using DubStudio.Core.Models;

namespace DubStudio.Core.Services;

public sealed record ArchiveEntry(string RelativePath, long Size, string? Sha256);

public sealed record ArchiveManifest(
    string ProjectName,
    string CreatedUtc,
    int Episodes,
    int Scenes,
    int Lines,
    int Takes,
    int ConfirmedPicks,
    bool IncludeAudio,
    List<ArchiveEntry> Entries);

public sealed record ArchiveResult(string ArchivePath, ArchiveManifest Manifest, int MissingOrChangedFiles);

/// <summary>工程归档：数据库 + 清单 + 可选交付音频，打成跨平台 zip；
/// 归档前做缺失/改动检测，归档后做条目往返校验。</summary>
public sealed class ArchiveService
{
    private readonly StudioDatabase _db;
    public ArchiveService(StudioDatabase db) => _db = db;

    public async Task<ArchiveResult> ArchiveAsync(string zipPath, bool includeAudio = true, CancellationToken ct = default)
    {
        var project = _db.RequireProject();
        var scanner = new AudioScanService(_db);
        var scan = await scanner.ScanAsync(project.Id, queueReviews: false, ct).ConfigureAwait(false);

        var lineIds = _db.Conn.Table<ScriptLine>().Where(l => l.ProjectId == project.Id).Select(l => l.Id).ToHashSet();
        var takes = _db.Conn.Table<Take>().ToList().Where(t => lineIds.Contains(t.LineId)).ToList();
        var picks = _db.Conn.Table<DirectorPick>().ToList().Where(p => lineIds.Contains(p.LineId)).ToList();
        var episodes = _db.Conn.Table<Episode>().Where(e => e.ProjectId == project.Id).Count();
        var sceneCount = _db.Conn.Table<Scene>().Where(s => s.ProjectId == project.Id).Count();
        var lineCount = lineIds.Count;
        var confirmed = picks.Count(p => p.Status == PickStatus.Confirmed);

        var entries = new List<ArchiveEntry>();
        // 以打包循环中实时检查为准（扫描只用于把问题推入复核队列并给出摘要）
        int bad = 0;

        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            // 数据库（先确保 WAL 落盘：另一只读连接不影响，主连接关闭时归档由调用方持有）
            var dbEntry = zip.CreateEntry("project.dsproj");
            await using (var es = dbEntry.Open())
                await using (var fs = new FileStream(_db.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    await fs.CopyToAsync(es, ct).ConfigureAwait(false);
            entries.Add(new ArchiveEntry("project.dsproj", new FileInfo(_db.FilePath).Length,
                await FileHasher.Sha256Async(_db.FilePath, ct).ConfigureAwait(false)));

            if (includeAudio)
            {
                var usedNames = new HashSet<string>();
                foreach (var take in takes.Where(t => !string.IsNullOrWhiteSpace(t.AudioFilePath)))
                {
                    ct.ThrowIfCancellationRequested();
                    var path = take.AudioFilePath!;
                    if (!File.Exists(path)) { bad++; continue; }

                    var rel = PackMediaName(path, usedNames);
                    var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);
                    await using (var es = entry.Open())
                    await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        await fs.CopyToAsync(es, ct).ConfigureAwait(false);

                    var info = new FileInfo(path);
                    var hash = await FileHasher.Sha256Async(path, ct).ConfigureAwait(false);
                    var ae = new ArchiveEntry(rel, info.Length, hash);
                    entries.Add(ae);

                    if (!string.IsNullOrEmpty(take.FileSha256) &&
                        !string.Equals(take.FileSha256, hash, StringComparison.OrdinalIgnoreCase))
                        bad++;
                }
            }

                var json = JsonSerializer.Serialize(new ArchiveManifest(project.Name, ReviewWorkflow.Now(),
                episodes, sceneCount, lineCount, takes.Count, confirmed, includeAudio, entries),
                new JsonSerializerOptions { WriteIndented = true });
            var manEntry = zip.CreateEntry("manifest.json");
            await using (var es = manEntry.Open())
            await using (var sw = new StreamWriter(es, Encoding.UTF8))
                await sw.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
        }

        // 往返校验：重开 zip 核对每个条目存在且非空
        VerifyRoundTrip(zipPath, includeAudio ? entries.Count : 2);
        var finalManifest = new ArchiveManifest(project.Name, ReviewWorkflow.Now(),
            episodes, sceneCount, lineCount, takes.Count, confirmed, includeAudio, entries);
        return new ArchiveResult(zipPath, finalManifest, bad);
    }

    private static string PackMediaName(string path, HashSet<string> used)
    {
        var candidate = "audio/" + Sanitize(Path.GetFileNameWithoutExtension(path)) + Path.GetExtension(path).ToLowerInvariant();
        var n = 1;
        while (!used.Add(candidate))
        {
            candidate = $"audio/{Sanitize(Path.GetFileNameWithoutExtension(path))}_{n++}{Path.GetExtension(path).ToLowerInvariant()}";
        }
        return candidate;
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
            sb.Append(ch == '/' || ch == '\\' || ch == ':' || ch == '*' || ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|' ? '_' : ch);
        return sb.ToString();
    }

    public static void VerifyRoundTrip(string zipPath, int expectedMinimumEntries)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries;
        if (!entries.Any(e => e.FullName == "project.dsproj"))
            throw new InvalidDataException("归档缺少 project.dsproj");
        if (!entries.Any(e => e.FullName == "manifest.json"))
            throw new InvalidDataException("归档缺少 manifest.json");
        if (entries.Count < expectedMinimumEntries)
            throw new InvalidDataException($"归档条目数异常: {entries.Count} < {expectedMinimumEntries}");
        foreach (var e in entries)
        {
            if (e.Length == 0 && e.FullName.EndsWith("/") == false)
                throw new InvalidDataException("归档中存在空条目: " + e.FullName);
        }
    }
}
