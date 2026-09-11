using DubStudio.Core.Data;
using DubStudio.Core.Import;
using DubStudio.Core.Models;

namespace DubStudio.Core.Services;

public sealed record SplitPart(
    string OriginalText,
    string? TranslatedText = null,
    double? InPointMs = null,
    double? MouthCloseMs = null,
    double? AccentMs = null,
    double? MaxDurationMs = null);

/// <summary>修订包的解析辅助：集/场/角色解析、相似度匹配、快照、ID 列表。</summary>
public sealed partial class RevisionPackageService
{
    private sealed class DraftRow
    {
        public required RevisionSceneDraft Scene { get; init; }
        public required RevisionLineDraft Line { get; init; }
        public string SceneKey { get; set; } = "";
        public string SceneCode { get; set; } = "";
        public string CharacterCode { get; set; } = "";
        public int? MatchedLineId { get; set; }
    }

    private List<DraftRow> ParseDraftRows(RevisionDraft draft, Episode? episode,
        List<Scene> scenes, List<Character> characters, int projectId)
    {
        var epCode = episode?.Code ?? ResolveEpisodeCode(episode);
        var epScenes = episode == null
            ? scenes
            : scenes.Where(s => s.EpisodeId == episode.Id).OrderBy(s => s.Ordinal).ToList();
        var rows = new List<DraftRow>();
        var pos = 0;
        foreach (var sdraft in draft.Scenes)
        {
            pos++;
            Scene? resolved = null;
            if (!string.IsNullOrWhiteSpace(sdraft.Code))
                resolved = scenes.FirstOrDefault(s => s.ProjectId == projectId && s.StableKey == $"{epCode}|{sdraft.Code!.Trim()}");
            resolved ??= !string.IsNullOrWhiteSpace(sdraft.Slug)
                ? scenes.FirstOrDefault(s => s.ProjectId == projectId && s.StableKey == $"{epCode}|#|{sdraft.Slug.Trim()}")
                : epScenes.Skip(pos - 1).FirstOrDefault();

            var sceneKey = resolved?.StableKey
                ?? (!string.IsNullOrWhiteSpace(sdraft.Code) ? $"{epCode}|{sdraft.Code!.Trim()}"
                    : !string.IsNullOrWhiteSpace(sdraft.Slug) ? $"{epCode}|#|{sdraft.Slug.Trim()}"
                    : $"{epCode}|#pos{pos}");
            var sceneCode = resolved?.Code
                ?? (!string.IsNullOrWhiteSpace(sdraft.Code) ? sdraft.Code!.Trim()
                    : StableIds.SceneCode((scenes.Count == 0 ? 0 : scenes.Max(s => s.Ordinal)) + pos));

            foreach (var ld in sdraft.Lines)
            {
                rows.Add(new DraftRow
                {
                    Scene = sdraft,
                    Line = ld,
                    SceneKey = sceneKey,
                    SceneCode = sceneCode,
                    CharacterCode = StableIds.CharacterCode(ld.Character, ld.Qualifier)
                });
            }
        }
        return rows;
    }

    private Episode? ResolveEpisode(string? code, int projectId)
    {
        var eps = _db.Conn.Table<Episode>().Where(e => e.ProjectId == projectId).ToList();
        if (eps.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(code))
            return eps.FirstOrDefault(e => e.Code == code!.Trim());
        if (eps.Count == 1) return eps[0];
        return eps.FirstOrDefault(e => e.Code == "E01") ?? eps[0];
    }

    private string ResolveEpisodeCode(Episode? episode)
    {
        if (episode != null) return episode.Code;
        var ep = _db.Conn.Table<Episode>().OrderBy(e => e.Ordinal).FirstOrDefault();
        return ep?.Code ?? "E01";
    }

    private static int? SceneEpisode(List<Scene> scenes, int sceneId)
        => scenes.FirstOrDefault(s => s.Id == sceneId)?.EpisodeId;

    private string CharacterCodeOf(ScriptLine line, List<Character> characters)
        => characters.FirstOrDefault(c => c.Id == line.CharacterId)?.StableCode ?? "";

    /// <summary>稳定身份比对文本：优先原文；原文为空时退化到当前译文。</summary>
    private static string IdentityText(ScriptLine line)
        => string.IsNullOrWhiteSpace(line.OriginalText) ? line.TranslatedText : line.OriginalText;

    /// <summary>新稿内同(场,角色,原文)出现序，与导入侧 occurrence 口径一致。</summary>
    private static int CountOccurrence(List<DraftRow> rows, DraftRow current)
    {
        var n = 0;
        foreach (var r in rows)
        {
            if (ReferenceEquals(r, current)) return n + 1;
            if (r.SceneKey == current.SceneKey && r.CharacterCode == current.CharacterCode
                && StableIds.TextHash(r.Line.Text) == StableIds.TextHash(current.Line.Text))
                n++;
        }
        return n + 1;
    }

    /// <summary>文本相似度：字符二元组 Dice 系数（对中日韩文与拉丁文都稳定），
    /// 短串辅以长度惩罚。用于在稳定键失配时识别“文字微调”而非误判删/增。</summary>
    public static double Similarity(string a, string b)
    {
        a = StableIds.Normalize(a);
        b = StableIds.Normalize(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var ga = Bigrams(a);
        var gb = Bigrams(b);
        if (ga.Count == 0 || gb.Count == 0) return 0;
        var overlap = ga.Count(gb.Contains);
        var dice = 2.0 * overlap / (ga.Count + gb.Count);
        var lenPenalty = 2.0 * Math.Min(a.Length, b.Length) / (a.Length + b.Length);
        return dice * 0.85 + lenPenalty * 0.15;
    }

    private static List<string> Bigrams(string s)
    {
        var list = new List<string>(Math.Max(0, s.Length - 1));
        for (var i = 0; i + 1 < s.Length; i++) list.Add(s.Substring(i, 2));
        if (list.Count == 0) list.Add(s);
        return list;
    }

    internal static string Csv(IEnumerable<int> ids) => string.Join(',', ids.Distinct());
    public static List<int> ParseIds(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? new()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

    /// <summary>快照台词时序/版本/状态、选用以及全部条次（含确认前状态，撤回时精确恢复）。</summary>
    private void SnapshotLine(RevisionChange ch, ScriptLine line, bool snapshotText)
    {
        if (snapshotText)
        {
            ch.SnapshotVersionId = line.CurrentVersionId;
            ch.SnapshotTranslatedText = line.TranslatedText;
        }
        ch.SnapshotInPointMs = line.InPointMs;
        ch.SnapshotMouthCloseMs = line.MouthCloseMs;
        ch.SnapshotAccentMs = line.AccentMs;
        ch.SnapshotMaxDurationMs = line.MaxDurationMs;
        ch.SnapshotLineState = line.State;
        var takes = _db.Conn.Table<Take>().Where(t => t.LineId == line.Id).OrderBy(t => t.Id).ToList();
        ch.AffectedTakeIds = Csv(takes.Select(t => t.Id));
        ch.SnapshotTakeStatuses = string.Join(',', takes.Select(t => t.Status.ToString()));
        var pick = _db.Conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == line.Id);
        if (pick != null)
        {
            ch.SnapshotPickTakeId = pick.TakeId;
            ch.SnapshotPickStatus = pick.Status;
            ch.SnapshotPickNote = pick.Note;
        }
    }
}
