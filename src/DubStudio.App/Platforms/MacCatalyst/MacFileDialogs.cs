using AppKit;
using DubStudio.App.Services;
using Foundation;

namespace DubStudio.App.Platforms.MacCatalyst;

/// <summary>macOS 上通过 NSOpenPanel/NSSavePanel 实现。</summary>
public sealed class MacFileDialogs : IFileDialogs
{
    public Task<string?> OpenFileAsync(string? filter = null) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            var panel = NSOpenPanel.OpenPanel;
            panel.CanChooseDirectories = false;
            panel.CanChooseFiles = true;
            panel.AllowsMultipleSelection = false;
            return panel.RunModal() == 1 ? panel.Url?.Path : null;
        });

    public Task<string?> SaveFileAsync(string suggestedName, string? extension = null) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            var panel = new NSSavePanel
            {
                NameFieldStringValue = suggestedName
            };
            return panel.RunModal() == 1 ? panel.Url?.Path : null;
        });
}
