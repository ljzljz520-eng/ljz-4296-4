using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubStudio.App.Services;
using DubStudio.Core.Models;

namespace DubStudio.App.ViewModels;

public partial class DirectorLineRow : ObservableObject
{
    public ScriptLine Line { get; }
    public string Label { get; }
    [ObservableProperty] private string _pickBadge;
    public DirectorLineRow(ScriptLine line, string label, string pickBadge)
    {
        Line = line; Label = label; _pickBadge = pickBadge;
    }
}

public partial class DirectorTakeRow
{
    public Take Take { get; }
    public string Summary => $"条{Take.TakeNo} {Take.Actor} {Take.StartMs:0}-{Take.EndMs:0}ms [{Take.Status}]";
    public bool FileMissing => string.IsNullOrWhiteSpace(Take.AudioFilePath) || !File.Exists(Take.AudioFilePath);
    public DirectorTakeRow(Take take) => Take = take;
}

public partial class DirectorViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    public ObservableCollectionEx<DirectorLineRow> Lines { get; } = new();
    public ObservableCollectionEx<DirectorTakeRow> Takes { get; } = new();

    [ObservableProperty] private DirectorLineRow? _selectedLine;
    [ObservableProperty] private DirectorTakeRow? _selectedTake;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _pickInfo = "";

    public DirectorViewModel(ProjectSession session)
    {
        _session = session;
        _session.Changed += Load;
        Load();
    }

    [RelayCommand]
    public void Load()
    {
        Lines.Clear();
        Takes.Clear();
        if (!_session.IsOpen) return;
        var db = _session.Require();
        var chars = db.Conn.Table<Character>().ToList().ToDictionary(c => c.Id, c => c.DisplayName);
        foreach (var line in db.Conn.Table<ScriptLine>().Where(l => l.State == LineState.Active)
                     .OrderBy(l => l.SceneId).ThenBy(l => l.SequenceInScene))
        {
            chars.TryGetValue(line.CharacterId, out var name);
            var pick = db.Conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == line.Id);
            var badge = pick == null ? "未选" : pick.Status == PickStatus.Confirmed ? "已确认" : "待复核";
            Lines.Add(new DirectorLineRow(line, $"{line.Code} {(name ?? "?")}：{line.OriginalText}", badge));
        }
        SelectedLine = Lines.FirstOrDefault();
    }

    partial void OnSelectedLineChanged(DirectorLineRow? value) => LoadTakes();

    private void LoadTakes()
    {
        Takes.Clear();
        PickInfo = "";
        if (SelectedLine == null) return;
        var db = _session.Require();
        foreach (var t in db.Conn.Table<Take>().Where(t => t.LineId == SelectedLine.Line.Id)
                     .Where(t => t.Status != TakeStatus.Rejected).OrderBy(t => t.TakeNo))
            Takes.Add(new DirectorTakeRow(t));
        var pick = db.Conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == SelectedLine.Line.Id);
        if (pick != null)
        {
            var take = db.Conn.Find<Take>(pick.TakeId);
            PickInfo = $"当前选用条 {take?.TakeNo} —— {pick.Status}";
            var match = Takes.FirstOrDefault(r => r.Take.Id == pick.TakeId);
            if (match != null) SelectedTake = match;
        }
    }

    [RelayCommand]
    private void Pick()
    {
        if (SelectedLine == null || SelectedTake == null) { StatusText = "请选择台词与条次"; return; }
        try
        {
            var pick = _session.Director().PickTake(SelectedLine.Line.Id, SelectedTake.Take.Id);
            StatusText = pick.Status == PickStatus.Confirmed ? "已采用并确认" : "已选用，但处于待复核（版本/状态不一致）";
            Load();
        }
        catch (Exception ex) { StatusText = "无法选用：" + ex.Message; }
    }

    [RelayCommand]
    private void Confirm()
    {
        if (SelectedLine == null) return;
        try
        {
            _session.Director().ConfirmPick(SelectedLine.Line.Id);
            StatusText = "选用已确认，相关复核项已关闭";
            Load();
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void ClearPick()
    {
        if (SelectedLine == null) return;
        _session.Director().ClearPick(SelectedLine.Line.Id);
        StatusText = "已清除选用";
        Load();
    }
}
