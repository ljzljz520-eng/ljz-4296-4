using DubStudio.App.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DubStudio.App.Platforms.Windows;

/// <summary>Windows 上通过 FileOpenPicker/FileSavePicker 实现。</summary>
public sealed class WindowsFileDialogs : IFileDialogs
{
    private static nint Hwnd()
    {
        var window = Application.Windows.FirstOrDefault()?.Handler?.PlatformView;
        return window == null ? 0 : WindowNative.GetWindowHandle((Microsoft.UI.Xaml.Window)window);
    }

    public async Task<string?> OpenFileAsync(string? filter = null)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, Hwnd());
        if (filter == null)
        {
            picker.FileTypeFilter.Add("*");
        }
        else
        {
            foreach (var ext in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                picker.FileTypeFilter.Add(ext.StartsWith('.') ? ext : "." + ext);
        }
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> SaveFileAsync(string suggestedName, string? extension = null)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName };
        InitializeWithWindow.Initialize(picker, Hwnd());
        var ext = extension ?? ".dsproj";
        picker.FileTypeChoices.Add("DubStudio 工程", new[] { ext });
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}
