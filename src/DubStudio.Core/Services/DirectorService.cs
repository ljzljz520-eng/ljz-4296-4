using DubStudio.Core.Data;
using DubStudio.Core.Models;

namespace DubStudio.Core.Services;

/// <summary>导演选用服务。强约束：只能选择真实存在的条次；
/// 条次被否决/待复核或台词已退役时，选用自动回到待复核。</summary>
public sealed class DirectorService
{
    private readonly StudioDatabase _db;
    public DirectorService(StudioDatabase db) => _db = db;

    public DirectorPick PickTake(int lineId, int takeId, string? note = null)
    {
        var line = _db.Conn.Find<ScriptLine>(lineId) ?? throw new InvalidOperationException("台词不存在");
        if (line.State == LineState.Retired)
            throw new InvalidOperationException("台词已退役，不能选用");
        var take = _db.Conn.Find<Take>(takeId) ?? throw new ArgumentException("条次不存在，不能凭空选用");
        if (take.LineId != lineId)
            throw new InvalidOperationException("条次不属于该台词");
        if (take.Status == TakeStatus.Rejected)
            throw new InvalidOperationException("该条次已被否决，不能选用");
        if (string.IsNullOrWhiteSpace(take.AudioFilePath) || !File.Exists(take.AudioFilePath))
            throw new InvalidOperationException("该条次缺少交付音频文件，不能选用");

        var pending = take.Status == TakeStatus.NeedsReview
                      || take.TextVersionId != line.CurrentVersionId
                      || _db.Conn.Table<ReviewItem>().Any(r => r.LineId == lineId && r.TakeId == takeId && !r.Resolved);

        var pick = _db.Conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == lineId);
        if (pick == null)
        {
            pick = new DirectorPick { LineId = lineId };
            _db.Conn.Insert(pick);
        }
        pick.TakeId = takeId;
        pick.Status = pending ? PickStatus.PendingReview : PickStatus.Confirmed;
        pick.Note = note;
        pick.UpdatedAt = ReviewWorkflow.Now();
        _db.Conn.Update(pick);

        if (pending)
            ReviewWorkflow.QueueReview(_db.Conn, lineId, takeId, ReviewKind.TextChanged,
                "选用的条次与当前译文/复核状态不一致，待确认");
        return pick;
    }

    /// <summary>导演确认某条次可沿用：条次恢复可用、选用确认、复核项关闭。</summary>
    public void ConfirmPick(int lineId)
    {
        var pick = _db.Conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == lineId)
                   ?? throw new InvalidOperationException("该台词尚无选用");
        var take = _db.Conn.Find<Take>(pick.TakeId) ?? throw new InvalidOperationException("选用条次已不存在");
        ReviewWorkflow.ConfirmTakeAfterReview(_db.Conn, take.Id);
        pick.Status = PickStatus.Confirmed;
        pick.UpdatedAt = ReviewWorkflow.Now();
        _db.Conn.Update(pick);
        foreach (var r in _db.Conn.Table<ReviewItem>().Where(r => r.LineId == lineId && !r.Resolved).ToList())
            ReviewWorkflow.Resolve(_db.Conn, r.Id);
    }

    public void ClearPick(int lineId)
    {
        var pick = _db.Conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == lineId);
        if (pick != null) _db.Conn.Delete(pick);
    }
}
