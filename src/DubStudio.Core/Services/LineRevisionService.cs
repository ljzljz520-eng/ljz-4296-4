using DubStudio.Core.Data;
using DubStudio.Core.Models;

namespace DubStudio.Core.Services;

/// <summary>译配人员工作服务：译文每次改动落一条不可变版本；口型关键点维护。</summary>
public sealed class LineRevisionService
{
    private readonly StudioDatabase _db;
    public LineRevisionService(StudioDatabase db) => _db = db;

    public LineTextVersion ReviseText(int lineId, string newText, string? note = null)
    {
        var line = _db.Conn.Find<ScriptLine>(lineId) ?? throw new InvalidOperationException("台词不存在");
        var normalized = newText ?? "";
        var latest = _db.Conn.Table<LineTextVersion>()
            .Where(v => v.LineId == lineId)
            .OrderByDescending(v => v.RevisionNo)
            .FirstOrDefault();
        var lastNo = latest?.RevisionNo ?? 0;

        if (latest != null && StableIds.Normalize(latest.Text) == StableIds.Normalize(normalized))
            return latest; // 内容无变化不产生新版本

        var version = new LineTextVersion
        {
            LineId = lineId,
            RevisionNo = lastNo + 1,
            Text = normalized,
            Note = note,
            CreatedAt = ReviewWorkflow.Now()
        };
        _db.Conn.Insert(version);

        line.TranslatedText = normalized;
        line.CurrentVersionId = version.Id;
        _db.Conn.Update(line);

        // 旧条次与导演选用不自动适用新版本
        if (lastNo >= 1)
        {
            ReviewWorkflow.InvalidateLine(_db.Conn, lineId,
                ReviewKind.TextChanged, $"译文已更新到第 {version.RevisionNo} 版，旧条次待复核", false);
        }
        TouchProject(line.ProjectId);
        return version;
    }

    public void UpdateLipSync(int lineId, double? inPointMs, double? mouthCloseMs, double? accentMs, double? maxDurationMs)
    {
        var line = _db.Conn.Find<ScriptLine>(lineId) ?? throw new InvalidOperationException("台词不存在");
        if (mouthCloseMs.HasValue && inPointMs.HasValue && mouthCloseMs < inPointMs)
            throw new ArgumentException("闭口点不能早于入点");
        if (maxDurationMs is < 0) throw new ArgumentException("长度限制不能为负");

        bool hadPoints = line.InPointMs.HasValue || line.MouthCloseMs.HasValue ||
                         line.AccentMs.HasValue || line.MaxDurationMs.HasValue;
        line.InPointMs = inPointMs;
        line.MouthCloseMs = mouthCloseMs;
        line.AccentMs = accentMs;
        line.MaxDurationMs = maxDurationMs;
        line.LipSyncDirty = false; // 人工重新标定后清除移位脏标
        _db.Conn.Update(line);

        // 仅当此前已有关键点且确实存在旧录音/选用时，调整才触发复核；首次标定不打扰
        var hasTakes = _db.Conn.Table<Take>().Any(t => t.LineId == lineId);
        var hasPick = _db.Conn.Table<DirectorPick>().Any(pk => pk.LineId == lineId);
        if (hadPoints && (hasTakes || hasPick))
        {
            ReviewWorkflow.InvalidateLine(_db.Conn, lineId,
                ReviewKind.ShiftedLine, "口型关键点已调整，选用条次待复核", false);
        }
        TouchProject(line.ProjectId);
    }

    private void TouchProject(int projectId)
    {
        var p = _db.Conn.Find<Project>(projectId);
        if (p != null) { p.UpdatedAt = ReviewWorkflow.Now(); _db.Conn.Update(p); }
    }
}
