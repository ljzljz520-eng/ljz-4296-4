using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubStudio.App.Services;
using DubStudio.Core.Models;

namespace DubStudio.App.ViewModels;

public partial class TakeRow : ObservableObject
{
    public Take Take { get; }
    public string LineCode { get; }
    [ObservableProperty] private TakeStatus _status;
    public TakeRow(Take take, string lineCode)
    {
        Take = take;
        LineCode = lineCode;
        _status = take.Status;
    }
    public string Summary => $"条{Take.TakeNo} {Take.Actor} / {Take.Microphone} " +
                             $"{Take.StartMs:0}-{Take.EndMs:0}ms";
    public string Audio => Take.AudioFilePath ?? "（无文件）";
    public string Note => Take.Note ?? "";
}

public partial class LinePickRow : ObservableObject
{
    public ScriptLine Line { get; }
    public string Label { get; }
    public LinePickRow(ScriptLine line, string label) { Line = line; Label = label; }
    public override string ToString() => Label;
}

public partial class RecordingViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly IFileDialogs _dialogs;

    public ObservableCollectionEx<LinePickRow> Lines { get; } = new();
    public ObservableCollectionEx<TakeRow> Takes { get; } = new();

    [ObservableProperty] private LinePickRow? _selectedLine;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _actor = "";
    [ObservableProperty] private string _microphone = "";
    [ObservableProperty] private double _startMs;
    [ObservableProperty] private double _endMs;
    [ObservableProperty] private string _audioPath = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private TakeRow? _selectedTake;

    public RecordingViewModel(ProjectSession session, IFileDialogs dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        _session.Changed += LoadLines;
        LoadLines();
    }

    public void LoadLines()
    {
        Lines.Clear();
        Takes.Clear();
        if (!_session.IsOpen) return;
        var db = _session.Require();
        var chars = db.Conn.Table<Character>().ToList().ToDictionary(c => c.Id, c => c.DisplayName);
        var scenes = db.Conn.Table<Scene>().ToList().ToDictionary(s => s.Id, s => s.Code);
        foreach (var l in db.Conn.Table<ScriptLine>().Where(x => x.State == LineState.Active)
                     .OrderBy(x => x.SceneId).ThenBy(x => x.SequenceInScene))
        {
            chars.TryGetValue(l.CharacterId, out var name);
            Lines.Add(new LinePickRow(l, $"{l.Code} {(name ?? "?")}：{l.OriginalText}"));
        }
        SelectedLine = Lines.FirstOrDefault();
    }

    partial void OnSelectedLineChanged(LinePickRow? value) => LoadTakes();

    private void LoadTakes()
    {
        Takes.Clear();
        if (SelectedLine == null) return;
        var db = _session.Require();
        foreach (var t in db.Conn.Table<Take>().Where(t => t.LineId == SelectedLine.Line.Id).OrderBy(t => t.TakeNo))
            Takes.Add(new TakeRow(t, SelectedLine.Line.Code));
        SelectedTake = Takes.FirstOrDefault();
    }

    [RelayCommand]
    private async Task BrowseAudioAsync()
    {
        var path = await _dialogs.OpenFileAsync(".wav,.flac,.mp3,.m4a,.aif,.aiff");
        if (path != null) AudioPath = path;
    }

    [RelayCommand]
    private async Task SaveTakeAsync()
    {
        if (SelectedLine == null) { StatusText = "请选择台词"; return; }
        try
        {
            var result = await _session.Recording().AddTakeAsync(new NewTakeRequest(
                SelectedLine.Line.Id, Actor, Microphone, StartMs, EndMs,
                string.IsNullOrWhiteSpace(AudioPath) ? null : AudioPath, Note));
            StatusText = result.NeedsReview ? "条次已保存，但因待复核/超长标记为待复核" : "条次已保存";
            LoadTakes();
        }
        catch (Exception ex) { StatusText = "保存失败：" + ex.Message; }
    }

    [RelayCommand]
    private void RejectTake()
    {
        if (SelectedTake == null) return;
        _session.Recording().RejectTake(SelectedTake.Take.Id);
        StatusText = "条次已否决；若被导演选用会转入待复核";
        LoadTakes();
    }

    [RelayCommand]
    private void MarkUsable()
    {
        if (SelectedTake == null) return;
        _session.Recording().MarkUsable(SelectedTake.Take.Id);
        StatusText = "条次已确认可用";
        LoadTakes();
    }
}

/// <summary>轻量封装，减少 using。</summary>
public sealed class ObservableCollectionEx<T> : System.Collections.ObjectModel.ObservableCollection<T> { }
