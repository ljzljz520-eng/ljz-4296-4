using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubStudio.App.Services;
using DubStudio.Core.Import;
using DubStudio.Core.Models;
using DubStudio.Core.Timecode;

namespace DubStudio.App.ViewModels;

public partial class LineRow : ObservableObject
{
    public ScriptLine Line { get; }
    public string CharacterName { get; }
    public int VersionNo { get; }

    [ObservableProperty] private string _translatedDraft;
    [ObservableProperty] private double? _inMs;
    [ObservableProperty] private double? _closeMs;
    [ObservableProperty] private double? _accentMs;
    [ObservableProperty] private double? _maxMs;
    [ObservableProperty] private string _stateBadge;

    public LineRow(ScriptLine line, string characterName, int versionNo)
    {
        Line = line;
        CharacterName = characterName;
        VersionNo = versionNo;
        _translatedDraft = line.TranslatedText;
        _inMs = line.InPointMs;
        _closeMs = line.MouthCloseMs;
        _accentMs = line.AccentMs;
        _maxMs = line.MaxDurationMs;
        _stateBadge = line.State == LineState.Retired ? "已退役"
            : line.LipSyncDirty ? "口型待复核" : "";
    }

    public string Code => Line.Code;
    public string Original => Line.OriginalText;
}

public partial class ScriptViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly IFileDialogs _dialogs;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _importInfo = "";
    [ObservableProperty] private LineRow? _selected;
    public System.Collections.ObjectModel.ObservableCollection<LineRow> Rows { get; } = new();

    public ScriptViewModel(ProjectSession session, IFileDialogs dialogs)
    {
        _session = session;
        _dialogs = dialogs;
        _session.Changed += Refresh;
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        try
        {
            var path = await _dialogs.OpenFileAsync(".txt,.json,.script");
            if (path == null) return;
            var content = File.ReadAllText(path);
            var doc = ScriptParser.Parse(content);
            var report = _session.Import().Import(doc);
            ImportInfo = $"集 {report.EpisodesTouched}｜新增句 {report.LinesCreated}｜更新 {report.LinesUpdated}" +
                         $"｜退役 {report.LinesRetired}｜恢复 {report.LinesRevived}｜待复核 {report.ReviewsQueued}";
            StatusText = report.Warnings.Count == 0 ? "导入完成" : string.Join("；", report.Warnings);
            Refresh();
        }
        catch (Exception ex) { StatusText = "导入失败：" + ex.Message; }
    }

    [RelayCommand]
    public void Refresh()
    {
        Rows.Clear();
        if (!_session.IsOpen) { StatusText = "请先打开工程"; return; }
        var db = _session.Require();
        var chars = db.Conn.Table<Character>().ToList().ToDictionary(c => c.Id, c => c.DisplayName);
        var lines = db.Conn.Table<ScriptLine>()
            .OrderBy(l => l.SceneId).ThenBy(l => l.SequenceInScene).ToList();
        foreach (var line in lines)
        {
            var vno = db.Conn.Find<LineTextVersion>(line.CurrentVersionId)?.RevisionNo ?? 0;
            chars.TryGetValue(line.CharacterId, out var name);
            Rows.Add(new LineRow(line, name ?? "?", vno));
        }
        StatusText = $"共 {Rows.Count} 句";
    }

    [RelayCommand]
    private void SaveTranslation()
    {
        if (Selected == null) return;
        try
        {
            _session.Lines().ReviseText(Selected.Line.Id, Selected.TranslatedDraft);
            StatusText = "已生成新版本（旧条次进入待复核）";
            Refresh();
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void SaveLipSync()
    {
        if (Selected == null) return;
        try
        {
            _session.Lines().UpdateLipSync(Selected.Line.Id,
                Selected.InMs, Selected.CloseMs, Selected.AccentMs, Selected.MaxMs);
            StatusText = "口型关键点已保存";
            Refresh();
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    [RelayCommand]
    private void TimecodeHint()
    {
        if (Selected == null) return;
        try
        {
            var p = _session.Require().RequireProject();
            var fr = new FrameRate(p.FrameRateNum, p.FrameRateDen);
            var parts = new List<string>();
            if (Selected.InMs is { } inMs) parts.Add("入点 " + fr.FrameToTimecode(fr.MillisecondsToFrame(inMs)));
            if (Selected.CloseMs is { } cMs) parts.Add("闭口 " + fr.FrameToTimecode(fr.MillisecondsToFrame(cMs)));
            if (Selected.AccentMs is { } aMs) parts.Add("重音 " + fr.FrameToTimecode(fr.MillisecondsToFrame(aMs)));
            StatusText = parts.Count == 0 ? "尚未填写关键点" : string.Join("｜", parts) +
                (p.VariableFrameRate ? "（VFR：帧号为标称近似，以毫秒为准）" : "");
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }
}
