namespace DubStudio.Core.Media;

public record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>抽象子进程执行，便于用假对象测试 FFmpeg 调用。</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default);
}

public sealed record MediaInfo(
    string Path,
    double DurationMs,
    double? AvgFrameRateNum,
    double? AvgFrameRateDen,
    bool HasVideo,
    bool HasAudio,
    bool VariableFrameRate,
    double? PacketSpacingCv);
