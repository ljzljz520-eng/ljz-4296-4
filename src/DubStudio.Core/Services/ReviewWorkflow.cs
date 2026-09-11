using DubStudio.Core.Models;
using SQLite;

namespace DubStudio.Core.Services;

/// <summary>统一的“旧录音不自动适用”策略：任何移位、换句、外部文件变化，
/// 都把相关条次置为待复核、导演选用置为待复核，并进入复核队列。</summary>
public static class ReviewWorkflow
{
    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    public static void QueueReview(SQLiteConnection conn, int lineId, int? takeId, ReviewKind kind, string message)
        => QueueReview(conn, lineId, takeId, kind, message, null);

    public static void QueueReview(SQLiteConnection conn, int lineId, int? takeId, ReviewKind kind, string message, int? packageId)
    {
        var exists = conn.Table<ReviewItem>().Any(r =>
            r.LineId == lineId && r.TakeId == takeId && r.Kind == kind && !r.Resolved
            && r.PackageId == packageId);
        if (!exists)
        {
            conn.Insert(new ReviewItem
            {
                LineId = lineId,
                TakeId = takeId,
                PackageId = packageId,
                Kind = kind,
                Message = message,
                CreatedAt = Now()
            });
        }
    }

    /// <summary>撤回修订包时回收其产生的、仍未解决的复核项。</summary>
    public static int WithdrawPackageReviews(SQLiteConnection conn, int packageId)
    {
        var items = conn.Table<ReviewItem>()
            .Where(r => r.PackageId == packageId && !r.Resolved).ToList();
        foreach (var r in items) conn.Delete(r);
        return items.Count;
    }

    /// <summary>使某条台词下的全部旧条次与选用进入待复核。</summary>
    public static int InvalidateLine(SQLiteConnection conn, int lineId, ReviewKind kind, string message, bool lipSyncDirty)
        => InvalidateLine(conn, lineId, kind, message, lipSyncDirty, null);

    /// <summary>使某条台词下的全部旧条次与选用进入待复核；packageId 非空时复核项归属修订包。
    /// 返回受影响（进入待复核）的条次 Id。</summary>
    public static List<int> InvalidateForPackage(SQLiteConnection conn, int lineId, ReviewKind kind, string message, bool lipSyncDirty, int packageId)
        => InvalidateLineCore(conn, lineId, kind, message, lipSyncDirty, packageId);

    public static int InvalidateLine(SQLiteConnection conn, int lineId, ReviewKind kind, string message, bool lipSyncDirty, int? packageId)
        => InvalidateLineCore(conn, lineId, kind, message, lipSyncDirty, packageId).Count;

    private static List<int> InvalidateLineCore(SQLiteConnection conn, int lineId, ReviewKind kind, string message, bool lipSyncDirty, int? packageId)
    {
        var affected = new List<int>();
        var line = conn.Find<ScriptLine>(lineId);
        if (line == null) return affected;
        if (lipSyncDirty)
        {
            line.LipSyncDirty = true;
            conn.Update(line);
        }

        var takes = conn.Table<Take>().Where(t => t.LineId == lineId).ToList();
        foreach (var take in takes)
        {
            if (take.Status == TakeStatus.Usable)
            {
                take.Status = TakeStatus.NeedsReview;
                conn.Update(take);
            }
            affected.Add(take.Id);
            QueueReview(conn, lineId, take.Id, kind, message, packageId);
        }

        var pick = conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == lineId);
        if (pick != null)
        {
            pick.Status = PickStatus.PendingReview;
            pick.UpdatedAt = Now();
            conn.Update(pick);
            QueueReview(conn, lineId, pick.TakeId, kind, "导演选用项待重新确认：" + message, packageId);
        }

        if (takes.Count == 0 && pick == null)
            QueueReview(conn, lineId, null, kind, message, packageId);

        return affected;
    }

    public static void Resolve(SQLiteConnection conn, int reviewId)
    {
        var item = conn.Find<ReviewItem>(reviewId);
        if (item == null || item.Resolved) return;
        item.Resolved = true;
        item.ResolvedAt = Now();
        conn.Update(item);
    }

    public static void ConfirmTakeAfterReview(SQLiteConnection conn, int takeId)
    {
        var take = conn.Find<Take>(takeId);
        if (take == null) return;
        take.Status = TakeStatus.Usable;
        conn.Update(take);
        foreach (var r in conn.Table<ReviewItem>().Where(r => r.TakeId == takeId && !r.Resolved).ToList())
            Resolve(conn, r.Id);
    }
}
