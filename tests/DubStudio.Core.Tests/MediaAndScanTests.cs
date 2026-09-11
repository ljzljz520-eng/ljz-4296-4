using DubStudio.Core.Media;
using DubStudio.Core.Models;
using DubStudio.Core.Services;
using Xunit;

namespace DubStudio.Core.Tests;

public class MediaAndScanTests
{
    private static async Task<(TestDb t, ScriptLine line, Take take, string audio)> SeedTakeAsync(byte[]? bytes = null)
    {
        var t = new TestDb();
        new ScriptImportService(t.Db).Import(Import.ScriptParser.Parse("""
EPISODE E01
SCENE S001
甲: 原句。
"""));
        var line = t.Db.Conn.Table<ScriptLine>().First();
        var audio = ImportNumberingTests.MakeAudio(t, "take.wav", bytes ?? new byte[] { 1, 2, 3, 4 });
        var take = (await new RecordingService(t.Db).AddTakeAsync(
            new NewTakeRequest(line.Id, "演员", "MIC", 0, 100, audio))).Take;
        return (t, line, take, audio);
    }

    [Fact]
    public async Task Scan_FindsMissingAudioFile()
    {
        var (t, line, take, audio) = await SeedTakeAsync();
        File.Delete(audio);
        var report = await new AudioScanService(t.Db).ScanAsync();
        Assert.Equal(1, report.Missing);
        Assert.Equal(AudioIssue.MissingFile, report.Items[0].Issue);
        Assert.Equal(TakeStatus.NeedsReview, t.Db.Conn.Find<Take>(take.Id).Status);
    }

    [Fact]
    public async Task Scan_DetectsExternalFileModification_AndFlagsReview()
    {
        var (t, line, take, audio) = await SeedTakeAsync(new byte[] { 1, 2, 3, 4 });
        new DirectorService(t.Db).PickTake(line.Id, take.Id);
        // 外部程序覆写文件（模拟棚外改动）
        await File.WriteAllBytesAsync(audio, new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 });
        var report = await new AudioScanService(t.Db).ScanAsync();
        Assert.Equal(1, report.Changed);
        Assert.Equal(AudioIssue.HashChanged, report.Items[0].Issue);
        Assert.Equal(TakeStatus.NeedsReview, t.Db.Conn.Find<Take>(take.Id).Status);
        Assert.Equal(PickStatus.PendingReview,
            t.Db.Conn.Table<DirectorPick>().First(p => p.LineId == line.Id).Status);
        Assert.True(t.Db.Conn.Table<ReviewItem>().Any(r =>
            r.LineId == line.Id && r.Kind == ReviewKind.ExternalFileChanged));
    }

    [Fact]
    public async Task Scan_CleanProject_HasNoIssues()
    {
        var (t, _, _, _) = await SeedTakeAsync();
        var report = await new AudioScanService(t.Db).ScanAsync();
        Assert.True(report.Clean);
        Assert.Empty(report.Items);
    }

    [Fact]
    public void Vfr_PacketSpacing_CfrStream_IsNotVfr()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 100; i++)
            sb.AppendLine((i / 25.0).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));
        var isVfr = FfmpegMediaService.DetectVfrFromPacketTimestamps(sb.ToString(), 0.05, out var cv);
        Assert.False(isVfr);
        Assert.True(cv < 0.01, $"CFR 变异系数应接近 0，实际 {cv}");
    }

    [Fact]
    public void Vfr_PacketSpacing_VfrStream_IsDetected()
    {
        var sb = new System.Text.StringBuilder();
        var t2 = 0.0;
        var rnd = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            sb.AppendLine(t2.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));
            t2 += (i % 5 == 0) ? 0.08 : 0.033 + rnd.NextDouble() * 0.01;
        }
        var isVfr = FfmpegMediaService.DetectVfrFromPacketTimestamps(sb.ToString(), 0.05, out var cv);
        Assert.True(isVfr, $"变异系数 {cv} 应超过阈值");
    }

    [Fact]
    public void Vfr_RateMismatch_FlaggedByProbeRates()
    {
        Assert.True(FfmpegMediaService.DetectVfrFromRates("30/1", "25/1"));
        Assert.False(FfmpegMediaService.DetectVfrFromRates("24/1", "24/1"));
        Assert.True(FfmpegMediaService.DetectVfrFromRates("30/1", "0/0"));
    }

    [Fact]
    public void Probe_ParsesJson_WithDurationStreamsAndVfr()
    {
        var json = """
        {
          "streams": [
            {"codec_type":"video","r_frame_rate":"30/1","avg_frame_rate":"24/1"},
            {"codec_type":"audio"}
          ],
          "format": {"duration":"12.5"}
        }
        """;
        var info = FfmpegMediaService.ParseProbe("x.mov", json);
        Assert.True(info.HasVideo && info.HasAudio);
        Assert.Equal(12500, info.DurationMs);
        Assert.True(info.VariableFrameRate);
        Assert.Equal(24, info.AvgFrameRateNum);
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public string? StdOut;
        public string? StdErr;
        public int ExitCode;
        public List<string>? LastArgs;
        public string? LastFile;
        public bool CreateOutputOnFfmpeg { get; set; } = true;

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            LastFile = fileName;
            LastArgs = arguments.ToList();
            if (fileName.Contains("ffmpeg") && CreateOutputOnFfmpeg)
            {
                var output = arguments[^1];
                File.WriteAllBytes(output, new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4 });
            }
            return Task.FromResult(new ProcessResult(ExitCode, StdOut ?? "", StdErr ?? ""));
        }
    }

    [Fact]
    public async Task ExtractAudio_InvokesSeparateFfmpegProcess_AndHashesOutput()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var output = System.IO.Path.Combine(dir, "out.wav");
        var fake = new FakeRunner();
        var svc = new FfmpegMediaService(fake, "ffmpeg", "ffprobe");

        var result = await svc.ExtractAudioAsync(System.IO.Path.Combine(dir, "in.mov"), output, 1500, 3000);

        Assert.True(File.Exists(output));
        Assert.NotNull(result.Sha256);
        Assert.True(fake.LastFile!.Contains("ffmpeg"));
        Assert.Contains("-ss", fake.LastArgs!);
        Assert.Contains("1.5", fake.LastArgs);
        Assert.Contains("-t", fake.LastArgs);
        Assert.Contains("3", fake.LastArgs);
        Assert.Equal("pcm_s16le", fake.LastArgs[fake.LastArgs.IndexOf("-acodec") + 1]);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task ExtractAudio_Failure_ThrowsWithStderr()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fake = new FakeRunner { ExitCode = 1, StdErr = "boom", CreateOutputOnFfmpeg = false };
        var svc = new FfmpegMediaService(fake, "ffmpeg", "ffprobe");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ExtractAudioAsync(System.IO.Path.Combine(dir, "in.mov"),
                System.IO.Path.Combine(dir, "out.wav")));
        Directory.Delete(dir, true);
    }
}
