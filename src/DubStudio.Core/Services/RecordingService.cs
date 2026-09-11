using DubStudio.Core.Data;
using DubStudio.Core.Media;
using DubStudio.Core.Models;

namespace DubStudio.Core.Services;

public sealed record NewTakeRequest(
    int LineId,
    string Actor,
    string Microphone,
    double StartMs,
    double EndMs,
    string? AudioFilePath,
    string? Note = null);

public sealed record TakeRecordResult(Take Take, bool NeedsReview);

/// <summary>录音师服务：为每个条次保存演员、麦克风、时间段、文件摘要与现场备注。</summary>
public sealed class RecordingService
{
    private readonly StudioDatabase _db;
    public RecordingService(StudioDatabase db) => _db = db;

    public async Task<TakeRecordResult> AddTakeAsync(NewTakeRequest req, CancellationToken ct = default)
    {
        if (req.EndMs <= req.StartMs) throw new ArgumentException("条次结束时间必须晚于开始时间");
        var line = _db.Conn.Find<ScriptLine>(req.LineId) ?? throw new InvalidOperationException("台词不存在");

        var takeQuery = _db.Conn.Table<Take>().Where(t => t.LineId == req.LineId);
        var takeNo = (takeQuery.Any() ? takeQuery.Max(t => t.TakeNo) : 0) + 1;

        string? hash = null;
        long size = 0;
        if (!string.IsNullOrWhiteSpace(req.AudioFilePath))
        {
            if (!File.Exists(req.AudioFilePath))
                throw new FileNotFoundException("录音文件不存在", req.AudioFilePath);
            hash = await FileHasher.Sha256Async(req.AudioFilePath!, ct).ConfigureAwait(false);
            size = new FileInfo(req.AudioFilePath!).Length;
        }

        // 台词处于移位/换句待复核状态时，新条次默认也需复核，避免误配。
        // 换演员复核项属于旧演员条次，不应阻止新演员的新批次录音。
        var openReviews = _db.Conn.Table<ReviewItem>()
            .Where(r => r.LineId == req.LineId && !r.Resolved).ToList();
        var needsReview = line.LipSyncDirty ||
                          openReviews.Any(r => r.Kind != ReviewKind.ActorChanged);

        // 角色已换演员：以历史演员名义录入的新条次不允许混入新批次，入待复核
        string? castWarning = null;
        var cast = new CastService(_db).GetCast(line.ProjectId, line.CharacterId);
        if (cast != null && !string.IsNullOrWhiteSpace(req.Actor) && req.Actor.Trim() != cast.Actor)
        {
            needsReview = true;
            castWarning = $"演员“{req.Actor.Trim()}”不是角色当前演员“{cast.Actor}”：历史录音保留，但该条不得混入新批次";
        }

        var take = new Take
        {
            LineId = req.LineId,
            TextVersionId = line.CurrentVersionId,
            TakeNo = takeNo,
            Actor = req.Actor?.Trim() ?? "",
            Microphone = req.Microphone?.Trim() ?? "",
            StartMs = req.StartMs,
            EndMs = req.EndMs,
            AudioFilePath = req.AudioFilePath,
            FileSha256 = hash,
            FileSizeBytes = size,
            Note = string.IsNullOrEmpty(castWarning) ? req.Note : $"{req.Note}｜{castWarning}",
            Status = needsReview ? TakeStatus.NeedsReview : TakeStatus.Usable,
            RecordedAt = ReviewWorkflow.Now()
        };
        _db.Conn.Insert(take);

        if (castWarning != null)
            ReviewWorkflow.QueueReview(_db.Conn, req.LineId, take.Id, ReviewKind.ActorChanged, castWarning);

        if (line.MaxDurationMs is > 0 && req.EndMs - req.StartMs > line.MaxDurationMs)
        {
            take.Status = TakeStatus.NeedsReview;
            _db.Conn.Update(take);
            ReviewWorkflow.QueueReview(_db.Conn, req.LineId, take.Id, ReviewKind.ShiftedLine,
                $"条次时长 {req.EndMs - req.StartMs:0}ms 超过长度限制 {line.MaxDurationMs:0}ms");
            needsReview = true;
        }

        return new TakeRecordResult(take, needsReview);
    }

    public void RejectTake(int takeId, string? reason = null)
    {
        var take = _db.Conn.Find<Take>(takeId);
        if (take == null) return;
        take.Status = TakeStatus.Rejected;
        _db.Conn.Update(take);
        var pick = _db.Conn.Table<DirectorPick>().FirstOrDefault(p => p.TakeId == takeId);
        if (pick != null)
        {
            pick.Status = PickStatus.PendingReview;
            pick.UpdatedAt = ReviewWorkflow.Now();
            _db.Conn.Update(pick);
            ReviewWorkflow.QueueReview(_db.Conn, pick.LineId, takeId, ReviewKind.ReimportConflict,
                "选用条次已被否决，请重选" + (reason == null ? "" : "：" + reason));
        }
    }

    public void MarkUsable(int takeId) => ReviewWorkflow.ConfirmTakeAfterReview(_db.Conn, takeId);
}
