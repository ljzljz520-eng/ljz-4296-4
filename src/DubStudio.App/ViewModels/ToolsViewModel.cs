using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubStudio.App.Services;
using DubStudio.Core.Services;

namespace DubStudio.App.ViewModels;

public partial class IssueRow
{
    public AudioIssueItem Item { get; }
    public string Text => $"句#{Item.LineId} 条{Item.TakeNo} [{Item.Issue}] {Item.Detail}";
    public IssueRow(AudioIssueItem item) => Item = item;
}

public partial class ToolsViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly IFileDialogs _dialogs;
    public ObservableCollectionEx<IssueRow> Issues { get; } = new();

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _includeAudio = true;

    public ToolsViewModel(ProjectSession session, IFileDialogs dialogs)
    {
        _session = session;
        _dialogs = dialogs;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!_session.IsOpen) { StatusText = "请先打开工程"; return; }
        try
        {
            StatusText = "正在扫描交付音频…";
            var report = await _session.Scan().ScanAsync();
            Issues.Clear();
            foreach (var i in report.Items) Issues.Add(new IssueRow(i));
            Summary = $"总条次 {report.TotalTakes}｜缺失 {report.Missing}｜被改动 {report.Changed}｜其他 {report.Other}";
            StatusText = report.Clean ? "扫描完成：交付音频齐全且未改动" : "发现问题，已入待复核队列";
        }
        catch (Exception ex) { StatusText = "扫描失败：" + ex.Message; }
    }

    [RelayCommand]
    private async Task ArchiveAsync()
    {
        if (!_session.IsOpen) { StatusText = "请先打开工程"; return; }
        try
        {
            var path = await _dialogs.SaveFileAsync(
                (_session.ProjectName) + "_交付.zip", ".zip");
            if (path == null) return;
            StatusText = "正在归档…";
            var result = await _session.Archive().ArchiveAsync(path, IncludeAudio);
            Summary = $"条目 {result.Manifest.Entries.Count}｜句 {result.Manifest.Lines}" +
                      $"｜条次 {result.Manifest.Takes}｜确认选用 {result.Manifest.ConfirmedPicks}" +
                      $"｜缺失/改动 {result.MissingOrChangedFiles}";
            StatusText = $"归档完成：{path}";
        }
        catch (Exception ex) { StatusText = "归档失败：" + ex.Message; }
    }
}
