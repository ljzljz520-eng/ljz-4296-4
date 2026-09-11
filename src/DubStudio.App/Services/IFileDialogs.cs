namespace DubStudio.App.Services;

/// <summary>桌面文件对话框抽象（Windows 原生 / Mac Catalyst 原生）。</summary>
public interface IFileDialogs
{
    Task<string?> OpenFileAsync(string? filter = null);
    Task<string?> SaveFileAsync(string suggestedName, string? extension = null);
}

public sealed class FallbackFileDialogs : IFileDialogs
{
    public Task<string?> OpenFileAsync(string? filter = null) =>
        Task.FromResult<string?>(null);

    public Task<string?> SaveFileAsync(string suggestedName, string? extension = null)
    {
        var safe = string.Concat(suggestedName.Split(Path.GetInvalidFileNameChars()));
        if (extension != null && !safe.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            safe += extension;
        return Task.FromResult<string?>(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), safe));
    }
}
