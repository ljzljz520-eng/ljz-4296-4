using DubStudio.Core.Data;
using DubStudio.Core.Models;

namespace DubStudio.Core.Services;

public sealed record CastBlockedTake(int TakeId, int LineId, int TakeNo, string RecordedActor, string CurrentActor);

/// <summary>角色演员指派。换演员不删除历史录音：历史条次保留可查，
/// 但与当前演员不符的条次不得混入新批次/新修订包交付，且换角时进入待复核。</summary>
public sealed class CastService
{
    private readonly StudioDatabase _db;
    public CastService(StudioDatabase db) => _db = db;

    /// <summary>设置（或更换）角色的当前演员；返回是否发生“换演员”。</summary>
    public bool SetActor(int characterId, string actor, string? note = null)
    {
        var name = actor?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("演员名不能为空");
        var character = _db.Conn.Find<Character>(characterId) ?? throw new InvalidOperationException("角色不存在");

        var current = GetCast(character.ProjectId, characterId);
        if (current != null && current.Actor == name)
        {
            current.Note = note ?? current.Note;
            _db.Conn.Update(current);
            return false;
        }

        var previousActor = current?.Actor;
        if (current == null)
        {
            _db.Conn.Insert(new CharacterCast
            {
                ProjectId = character.ProjectId,
                CharacterId = characterId,
                Actor = name,
                Note = note,
                ChangedAt = ReviewWorkflow.Now()
            });
        }
        else
        {
            current.Actor = name;
            current.Note = note;
            current.ChangedAt = ReviewWorkflow.Now();
            _db.Conn.Update(current);
        }

        // 首次指派不打扰；换演员才把旧演员的历史录音推入复核（保留，不删除）
        if (previousActor != null)
        {
            var lineIds = _db.Conn.Table<ScriptLine>()
                .Where(l => l.CharacterId == characterId).Select(l => l.Id).ToHashSet();
            foreach (var take in _db.Conn.Table<Take>().ToList().Where(t => lineIds.Contains(t.LineId)))
            {
                if (take.Actor != name)
                {
                    if (take.Status == TakeStatus.Usable)
                    {
                        take.Status = TakeStatus.NeedsReview;
                        _db.Conn.Update(take);
                    }
                    ReviewWorkflow.QueueReview(_db.Conn, take.LineId, take.Id, ReviewKind.ActorChanged,
                        $"角色“{character.DisplayName}”已从 {previousActor} 换为 {name}，旧演员条次保留但不可混入新批次");
                    var pick = _db.Conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == take.LineId && p.TakeId == take.Id);
                    if (pick != null && pick.Status == PickStatus.Confirmed)
                    {
                        pick.Status = PickStatus.PendingReview;
                        pick.UpdatedAt = ReviewWorkflow.Now();
                        _db.Conn.Update(pick);
                    }
                }
            }
            Touch(character.ProjectId);
        }
        return previousActor != null;
    }

    public CharacterCast? GetCast(int projectId, int characterId) =>
        _db.Conn.Table<CharacterCast>()
            .FirstOrDefault(c => c.ProjectId == projectId && c.CharacterId == characterId);

    /// <summary>条次的录音演员是否为角色当前演员；角色从未指派演员时不拦截（兼容老工程）。</summary>
    public bool IsCurrentActor(Take take)
    {
        var line = _db.Conn.Find<ScriptLine>(take.LineId);
        if (line == null) return true;
        var cast = GetCast(line.ProjectId, line.CharacterId);
        if (cast == null) return true;
        return string.Equals(cast.Actor, take.Actor, StringComparison.Ordinal);
    }

    /// <summary>批量找出“历史演员”条次（不可混入新批次）。</summary>
    public List<CastBlockedTake> FindBlockedTakes(IEnumerable<int> lineIds)
    {
        var set = lineIds.ToHashSet();
        var lines = _db.Conn.Table<ScriptLine>().Where(l => set.Contains(l.Id)).ToList();
        var blocked = new List<CastBlockedTake>();
        foreach (var take in _db.Conn.Table<Take>().ToList().Where(t => set.Contains(t.LineId)))
        {
            var line = lines.FirstOrDefault(l => l.Id == take.LineId);
            if (line == null) continue;
            var cast = GetCast(line.ProjectId, line.CharacterId);
            if (cast != null && take.Actor != cast.Actor)
                blocked.Add(new CastBlockedTake(take.Id, take.LineId, take.TakeNo, take.Actor, cast.Actor));
        }
        return blocked;
    }

    private void Touch(int projectId)
    {
        var p = _db.Conn.Find<Project>(projectId);
        if (p != null) { p.UpdatedAt = ReviewWorkflow.Now(); _db.Conn.Update(p); }
    }
}
