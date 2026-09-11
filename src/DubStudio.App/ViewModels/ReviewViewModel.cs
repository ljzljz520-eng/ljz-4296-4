using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DubStudio.App.Services;
using DubStudio.Core.Models;
using DubStudio.Core.Services;

namespace DubStudio.App.ViewModels;

public partial class ReviewRow
{
    public ReviewItem Item { get; }
    public string LineCode { get; }
    public string KindText => Item.Kind switch
    {
        ReviewKind.TextChanged => "译文已改",
        ReviewKind.ShiftedLine => "对白移位",
        ReviewKind.ReimportConflict => "再导入冲突",
        ReviewKind.ExternalFileChanged => "外部文件改动",
        _ => Item.Kind.ToString()
    };
    public ReviewRow(ReviewItem item, string lineCode) { Item = item; LineCode = lineCode; }
}

public partial class ReviewViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    public ObservableCollectionEx<ReviewRow> Items { get; } = new();
    [ObservableProperty] private ReviewRow? _selected;
    [ObservableProperty] private string _statusText = "";

    public ReviewViewModel(ProjectSession session)
    {
        _session = session;
        _session.Changed += Load;
        Load();
    }

    [RelayCommand]
    public void Load()
    {
        Items.Clear();
        if (!_session.IsOpen) return;
        var db = _session.Require();
        var lines = db.Conn.Table<ScriptLine>().ToList().ToDictionary(l => l.Id, l => l.Code);
        foreach (var r in db.Conn.Table<ReviewItem>().Where(r => !r.Resolved)
                     .OrderBy(r => r.LineId).ThenBy(r => r.Id))
        {
            lines.TryGetValue(r.LineId, out var code);
            Items.Add(new ReviewRow(r, code ?? ("#" + r.LineId)));
        }
        StatusText = Items.Count == 0 ? "没有待复核项" : $"{Items.Count} 项待复核";
    }

    [RelayCommand]
    private void ConfirmTake()
    {
        if (Selected?.Item.TakeId is not int takeId) { StatusText = "该项不关联条次"; return; }
        ReviewWorkflow.ConfirmTakeAfterReview(_session.Require().Conn, takeId);
        var pick = _session.Require().Conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == Selected.Item.LineId);
        if (pick != null && pick.TakeId == takeId)
        {
            pick.Status = PickStatus.Confirmed;
            pick.UpdatedAt = ReviewWorkflow.Now();
            _session.Require().Conn.Update(pick);
        }
        ReviewWorkflow.Resolve(_session.Require().Conn, Selected.Item.Id);
        StatusText = "已确认旧录音可沿用";
        Load();
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (Selected == null) return;
        ReviewWorkflow.Resolve(_session.Require().Conn, Selected.Item.Id);
        Load();
    }
}
