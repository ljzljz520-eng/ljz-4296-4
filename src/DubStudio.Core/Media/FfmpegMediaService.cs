using System.Globalization;
using System.Text.Json;

namespace DubStudio.Core.Media;

public record ExtractResult(string OutputPath, string? Sha256, long FileSizeBytes);

/// <summary>FFmpeg 独立进程媒体服务：探测帧率/VFR、抽取音频。所有解码均委托外部进程。</summary>
public sealed class FfmpegMediaService
{
    private readonly IProcessRunner _runner;
    private readonly Func<string> _ffmpegPath;
    private readonly Func<string> _ffprobePath;

    public FfmpegMediaService(IProcessRunner runner, string? ffmpegPath = null, string? ffprobePath = null)
    {
        _runner = runner;
        _ffmpegPath = () => ffmpegPath ?? Default("ffmpeg");
        _ffprobePath = () => ffprobePath ?? Default("ffprobe");
    }

    private static string Default(string tool)
    {
        if (OperatingSystem.IsWindows()) return tool + ".exe";
        if (OperatingSystem.IsMacOS()) return $"/usr/local/bin/{tool}";
        return $"/usr/bin/{tool}";
    }

    public string FfmpegPath => _ffmpegPath();
    public string FfprobePath => _ffprobePath();

    /// <summary>探测媒体：时长、是否含音视频、平均帧率、以及是否可变帧率。</summary>
    public async Task<MediaInfo> ProbeAsync(string mediaPath, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            mediaPath
        };
        var result = await _runner.RunAsync(_ffprobePath(), args, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"ffprobe 失败: {result.StdErr}");
        return ParseProbe(mediaPath, result.StdOut);
    }

    public static MediaInfo ParseProbe(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double durationMs = 0;
        bool hasVideo = false, hasAudio = false;
        double? avgNum = null, avgDen = null;
        string? rFrameRate = null, avgFrameRate = null;

        if (root.TryGetProperty("format", out var fmt) &&
            fmt.TryGetProperty("duration", out var dur))
        {
            durationMs = double.Parse(dur.GetString() ?? "0", CultureInfo.InvariantCulture) * 1000.0;
        }

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var st in streams.EnumerateArray())
            {
                var codec = st.TryGetProperty("codec_type", out var ct2) ? ct2.GetString() : null;
                if (codec == "video")
                {
                    hasVideo = true;
                    if (st.TryGetProperty("r_frame_rate", out var rf)) rFrameRate = rf.GetString();
                    if (st.TryGetProperty("avg_frame_rate", out var af)) avgFrameRate = af.GetString();
                }
                else if (codec == "audio")
                {
                    hasAudio = true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(avgFrameRate) && avgFrameRate != "0/0")
        {
            var p = avgFrameRate.Split('/');
            if (p.Length == 2
                && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var an)
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ad)
                && ad > 0)
            {
                avgNum = an; avgDen = ad;
            }
        }

        bool vfr = DetectVfrFromRates(rFrameRate, avgFrameRate);
        return new MediaInfo(path, durationMs, avgNum, avgDen, hasVideo, hasAudio, vfr, null);
    }

    /// <summary>粗检：ffprobe 报告 r_frame_rate 与 avg_frame_rate 明显不同即疑似 VFR。
    /// 权威检测请使用 AnalyzePacketSpacingAsync。</summary>
    public static bool DetectVfrFromRates(string? rFrameRate, string? avgFrameRate)
    {
        if (string.IsNullOrWhiteSpace(rFrameRate) || string.IsNullOrWhiteSpace(avgFrameRate)) return false;
        if (avgFrameRate == "0/0") return true;
        var a = ParseFraction(rFrameRate);
        var b = ParseFraction(avgFrameRate);
        if (a == null || b == null || b.Value.rate <= 0) return false;
        return Math.Abs(a.Value.rate - b.Value.rate) / b.Value.rate > 0.02;
    }

    private static (double num, double den, double rate)? ParseFraction(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split('/');
        if (p.Length != 2) return null;
        if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ||
            !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d == 0)
            return null;
        return (n, d, n / d);
    }

    /// <summary>权威 VFR 检测：读取全部视频包的 PTS（毫秒），计算相邻间隔的变异系数。
    /// cv 超过阈值（默认 0.05）判为 VFR。阈值可配，兼顾测试。</summary>
    public async Task<bool> AnalyzePacketSpacingAsync(string mediaPath, double cvThreshold = 0.05, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "packet=pts_time",
            "-of", "csv=p=0",
            mediaPath
        };
        var result = await _runner.RunAsync(_ffprobePath(), args, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"ffprobe 包分析失败: {result.StdErr}");
        return DetectVfrFromPacketTimestamps(result.StdOut, cvThreshold, out _);
    }

    public static bool DetectVfrFromPacketTimestamps(string csv, double cvThreshold, out double cv)
    {
        var stamps = new List<double>();
        foreach (var raw in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("N/A", StringComparison.OrdinalIgnoreCase)) continue;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                stamps.Add(t);
        }
        cv = 0;
        if (stamps.Count < 4) return false;

        var deltas = new List<double>(stamps.Count - 1);
        for (var i = 1; i < stamps.Count; i++)
        {
            var d = stamps[i] - stamps[i - 1];
            if (d > 0) deltas.Add(d);
        }
        if (deltas.Count < 3) return false;

        var mean = deltas.Average();
        if (mean <= 0) return false;
        var variance = deltas.Sum(d => (d - mean) * (d - mean)) / deltas.Count;
        cv = Math.Sqrt(variance) / mean;
        return cv > cvThreshold;
    }

    /// <summary>从源媒体抽取音频（默认 WAV 48kHz 16bit 单声道，便于棚内使用）。
    /// 时间参数为毫秒时间轴，对 VFR 源同样以真实时间抽取。</summary>
    public async Task<ExtractResult> ExtractAudioAsync(string sourcePath, string outputPath,
        double? startMs = null, double? durationMs = null, CancellationToken ct = default)
    {
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        if (startMs is > 0)
        {
            args.Add("-ss");
            args.Add((startMs.Value / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        }
        args.Add("-i");
        args.Add(sourcePath);
        if (durationMs is > 0)
        {
            args.Add("-t");
            args.Add((durationMs.Value / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        }
        args.AddRange(new[]
        {
            "-vn", "-acodec", "pcm_s16le", "-ar", "48000", "-ac", "1",
            outputPath
        });

        var result = await _runner.RunAsync(_ffmpegPath(), args, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"ffmpeg 抽音频失败: {result.StdErr}");
        if (!File.Exists(outputPath))
            throw new FileNotFoundException("ffmpeg 未生成输出文件", outputPath);

        var info = new FileInfo(outputPath);
        var hash = await FileHasher.Sha256Async(outputPath, ct).ConfigureAwait(false);
        return new ExtractResult(outputPath, hash, info.Length);
    }
}
