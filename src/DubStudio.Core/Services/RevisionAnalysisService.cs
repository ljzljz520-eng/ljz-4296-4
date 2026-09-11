using DubStudio.Core.Data;
using DubStudio.Core.Import;
using DubStudio.Core.Models;

namespace DubStudio.Core.Services;

public sealed record RevisionAnalysisReport(
    int PackageId,
    string PackageCode,
    double TimelineShiftMs,
    int TextTweaks,
    int DurationChanges,
    int TextAndDuration,
    int Deleted,
    int Added,
    int Split,
    int Merged,
    int SuggestedRerecord,
    int SuggestedReedit,
    int SuggestedKeep,
    int SuggestedRetire,
    int SuggestedNew,
    List<RevisionChange> Changes)
{
    public int TotalChanges => Changes.Count;
    public bool Empty => Changes.Count == 0 && TimelineShiftMs == 0;
}

/// <summary>混录后修订影响分析：译配编辑提交新稿时，按稳定语句身份
/// 区分文字微调 / 时长变化 / 删句 / 新增句，并圈出每个条次“需补录 / 仅需重剪 / 可以保留”。
/// 分析只产出修订包（草案），不改动在制数据；声音导演逐条确认处置后才生效。</summary>
public sealed partial class RevisionPackageService
{
    private readonly StudioDatabase _db;
    public RevisionPackageService(StudioDatabase db) => _db = db;

    public const double SimilarityThreshold = 0.5;

    /// <summary>创建不附带新稿的空修订包，供编辑显式执行拆分/合并等结构操作。</summary>
    public RevisionAnalysisReport CreateStructuralPackage(int projectId = 1, string? reason = null,
        string? submittedBy = null, double timelineShiftMs = 0)
    {
        var package = new RevisionPackage
        {
            ProjectId = projectId,
            Ordinal = NextOrdinal(projectId),
            Reason = reason ?? "",
            SubmittedBy = submittedBy ?? "",
            State = RevisionState.Draft,
            CreatedAt = ReviewWorkflow.Now(),
            TimelineShiftMs = timelineShiftMs
        };
        package.Code = "REV-" + package.Ordinal;
        _db.InTransaction(() =>
        {
            _db.RequireProject(projectId);
            _db.Conn.Insert(package);
        });
        return MakeReport(package, new List<RevisionChange>(), 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>提交新稿并完成影响分析，生成一个草案修订包。</summary>
    public RevisionAnalysisReport Analyze(RevisionDraft draft, int projectId = 1, string? reason = null, string? submittedBy = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Scenes.Sum(s => s.Lines.Count) == 0)
            throw new ArgumentException("修订稿没有任何台词行");

        var package = new RevisionPackage
        {
            ProjectId = projectId,
            Ordinal = NextOrdinal(projectId),
            Reason = reason ?? draft.Reason ?? "",
            SubmittedBy = submittedBy ?? draft.SubmittedBy ?? "",
            State = RevisionState.Draft,
            CreatedAt = ReviewWorkflow.Now(),
            TimelineShiftMs = draft.TimelineShiftMs
        };
        package.Code = "REV-" + package.Ordinal;

        RevisionAnalysisReport report = null!;
        _db.InTransaction(() =>
        {
            _db.RequireProject(projectId);
            _db.Conn.Insert(package);

            var episode = ResolveEpisode(draft.EpisodeCode, projectId);
            var activeLines = _db.Conn.Table<ScriptLine>()
                .Where(l => l.ProjectId == projectId && l.State == LineState.Active).ToList();
            var scenes = _db.Conn.Table<Scene>().Where(s => s.ProjectId == projectId).ToList();
            var characters = _db.Conn.Table<Character>().Where(c => c.ProjectId == projectId).ToList();

            // 解析新稿：每一行解析为 (场景, 角色, 行稿)，并按在制数据建立候选池
            var parsed = ParseDraftRows(draft, episode, scenes, characters, projectId);
            var unmatchedOld = activeLines
                .Where(l => episode == null || l.SceneId == episode.Id || SceneEpisode(scenes, l.SceneId) == episode.Id)
                .ToHashSet();

            int textTweak = 0, dur = 0, both = 0, added = 0;

            // 第一轮：稳定键（场景|角色|原文哈希|出现序）精确匹配
            foreach (var row in parsed)
            {
                var occ = CountOccurrence(parsed, row);
                var key = StableIds.LineKey(episode?.Code ?? ResolveEpisodeCode(episode),
                    row.SceneCode, row.CharacterCode, row.Line.Text, occ);
                var exact = unmatchedOld.FirstOrDefault(l => l.StableKey == key);
                if (exact != null)
                {
                    unmatchedOld.Remove(exact);
                    var kind = ClassifyChanged(exact, row.Line, out var basis);
                    if (kind != null)
                    {
                        var ch = BuildChangedChange(package.Id, exact, row.Line, kind.Value, basis);
                        _db.Conn.Insert(ch);
                        if (kind == LineChangeKind.TextTweak) textTweak++;
                        else if (kind == LineChangeKind.DurationChanged) dur++;
                        else both++;
                    }
                    row.MatchedLineId = exact.Id;
                }
            }

            // 第二轮：同(场景,角色)分组内按文本相似度贪心匹配 → 文字微调；配不上的是新增句
            var groups = parsed.Where(r => r.MatchedLineId == null)
                .GroupBy(r => (r.SceneKey, r.CharacterCode));
            foreach (var g in groups)
            {
                var pool = unmatchedOld.Where(l =>
                {
                    var sc = scenes.FirstOrDefault(s => s.Id == l.SceneId);
                    return sc != null && sc.StableKey == g.Key.SceneKey
                           && CharacterCodeOf(l, characters) == g.Key.CharacterCode;
                }).ToList();

                foreach (var row in g.ToList())
                {
                    var best = (line: (ScriptLine?)null, score: 0.0);
                    foreach (var cand in pool)
                    {
                        var score = Similarity(IdentityText(cand), row.Line.Text);
                        if (score > best.score) best = (cand, score);
                    }
                    if (best.line != null && best.score >= SimilarityThreshold)
                    {
                        pool.Remove(best.line);
                        unmatchedOld.Remove(best.line);
                        row.MatchedLineId = best.line.Id;
                        var kind = ClassifyChanged(best.line, row.Line, out var basis, fuzzyOriginal: true);
                        var ch = BuildChangedChange(package.Id, best.line, row.Line,
                            kind ?? LineChangeKind.TextTweak,
                            kind == null
                                ? $"原文经编辑改写（相似度 {best.score:0.00}），判定为文字微调，需补录"
                                : $"稳定身份匹配（相似度 {best.score:0.00}）：{basis}");
                        _db.Conn.Insert(ch);
                        textTweak++;
                    }
                    else
                    {
                        var ch = BuildAddedChange(package.Id, row, episode?.Code ?? ResolveEpisodeCode(episode));
                        _db.Conn.Insert(ch);
                        added++;
                    }
                }
            }

            // 第三轮：仍未匹配上的在制句 = 删句（稳定身份消失）
            int deleted = 0;
            foreach (var gone in unmatchedOld.OrderBy(l => l.Id).ToList())
            {
                _db.Conn.Insert(BuildDeletedChange(package.Id, gone, characters, scenes));
                deleted++;
            }

            // 时间轴整体平移：为带录音或选用的未变句圈出“仅需重剪”
            var shiftLineIds = new List<int>();
            var shiftTakeIds = new List<int>();
            if (draft.TimelineShiftMs != 0)
            {
                var changeLineIds = _db.Conn.Table<RevisionChange>().Where(c => c.PackageId == package.Id)
                    .Select(c => c.LineId).ToHashSet();
                foreach (var l in activeLines.Where(l => !changeLineIds.Contains(l.Id)))
                {
                    if (episode != null && SceneEpisode(scenes, l.SceneId) != episode.Id) continue;
                    var takes = _db.Conn.Table<Take>().Where(t => t.LineId == l.Id).ToList();
                    var hasPick = _db.Conn.Table<DirectorPick>().Any(p => p.LineId == l.Id);
                    if (takes.Count == 0 && !hasPick) continue;
                    var ch = new RevisionChange
                    {
                        PackageId = package.Id,
                        LineId = l.Id,
                        Kind = LineChangeKind.DurationChanged,
                        SuggestedDisposition = LineDisposition.ReeditOnly,
                        Disposition = LineDisposition.Undecided,
                        SuggestionBasis = $"时间轴整体平移 {draft.TimelineShiftMs:0}ms，录音内容不变，仅需重剪对齐"
                    };
                    SnapshotLine(ch, l, snapshotText: false); // 同时记录受影响条次与其确认前状态
                    _db.Conn.Insert(ch);
                    shiftLineIds.Add(l.Id);
                    shiftTakeIds.AddRange(takes.Select(t => t.Id));
                    dur++;
                }
                package.TimelineShiftMs = draft.TimelineShiftMs;
                package.ShiftLineIds = Csv(shiftLineIds);
                package.ShiftTakeIds = Csv(shiftTakeIds);
            }

            var all = _db.Conn.Table<RevisionChange>().Where(c => c.PackageId == package.Id).ToList();
            _db.Conn.Update(package);
            report = MakeReport(package, all, textTweak, dur, both, deleted, added, 0, 0);
        });

        return report;
    }

    private RevisionChange BuildChangedChange(int packageId, ScriptLine line, RevisionLineDraft d,
        LineChangeKind kind, string basis)
    {
        var takes = _db.Conn.Table<Take>().Where(t => t.LineId == line.Id).ToList();
        var ch = new RevisionChange
        {
            PackageId = packageId,
            LineId = line.Id,
            Kind = kind,
            NewOriginalText = kind != LineChangeKind.DurationChanged ? d.Text : line.OriginalText,
            NewTranslatedText = d.TranslatedText,
            NewInPointMs = d.InPointMs,
            NewMouthCloseMs = d.MouthCloseMs,
            NewAccentMs = d.AccentMs,
            NewMaxDurationMs = d.MaxDurationMs,
            SuggestionBasis = basis,
            AffectedTakeIds = Csv(takes.Select(t => t.Id))
        };
        ch.SuggestedDisposition = SuggestFor(kind, takes.Count > 0);
        SnapshotLine(ch, line, snapshotText: true);
        return ch;
    }

    private RevisionChange BuildAddedChange(int packageId, DraftRow row, string episodeCode)
    {
        return new RevisionChange
        {
            PackageId = packageId,
            LineId = 0,
            Kind = LineChangeKind.Added,
            TargetSceneKey = row.SceneKey,
            TargetCharacterName = row.Line.Character,
            TargetQualifier = row.Line.Qualifier,
            NewOriginalText = row.Line.Text,
            NewTranslatedText = row.Line.TranslatedText,
            NewInPointMs = row.Line.InPointMs,
            NewMouthCloseMs = row.Line.MouthCloseMs,
            NewAccentMs = row.Line.AccentMs,
            NewMaxDurationMs = row.Line.MaxDurationMs,
            SuggestedDisposition = LineDisposition.NewRecording,
            SuggestionBasis = "新稿中出现的新增句，稳定身份无历史录音，等待补录"
        };
    }

    private RevisionChange BuildDeletedChange(int packageId, ScriptLine line, List<Character> characters, List<Scene> scenes)
    {
        var takes = _db.Conn.Table<Take>().Where(t => t.LineId == line.Id).ToList();
        var ch = new RevisionChange
        {
            PackageId = packageId,
            LineId = line.Id,
            Kind = LineChangeKind.Deleted,
            SuggestedDisposition = LineDisposition.Retire,
            SuggestionBasis = "新稿中该稳定语句身份消失（删句），原录音不再交付",
            AffectedTakeIds = Csv(takes.Select(t => t.Id))
        };
        SnapshotLine(ch, line, snapshotText: false);
        return ch;
    }

    private LineChangeKind? ClassifyChanged(ScriptLine line, RevisionLineDraft d, out string basis, bool fuzzyOriginal = false)
    {
        bool textChanged = fuzzyOriginal
            ? StableIds.Normalize(IdentityText(line)) != StableIds.Normalize(d.Text)
              || (d.TranslatedText != null && StableIds.Normalize(d.TranslatedText) != StableIds.Normalize(line.TranslatedText))
            : (d.TranslatedText != null && StableIds.Normalize(d.TranslatedText) != StableIds.Normalize(line.TranslatedText))
              || (StableIds.Normalize(d.Text) != StableIds.Normalize(line.OriginalText)
                  && d.TranslatedText == null);
        bool timingChanged = d.InPointMs != null && NullDiff(line.InPointMs, d.InPointMs)
            || d.MouthCloseMs != null && NullDiff(line.MouthCloseMs, d.MouthCloseMs)
            || d.AccentMs != null && NullDiff(line.AccentMs, d.AccentMs)
            || d.MaxDurationMs != null && NullDiff(line.MaxDurationMs, d.MaxDurationMs);

        if (textChanged && timingChanged)
        {
            basis = "文字与时长同时变化：旧录音对不上新口型，需补录";
            return LineChangeKind.TextAndDuration;
        }
        if (textChanged)
        {
            basis = "文字微调（时长参数未变）：旧录音念的是旧词，需补录";
            return LineChangeKind.TextTweak;
        }
        if (timingChanged)
        {
            basis = "时长/口型点变化（文字未变）：录音内容可沿用，仅需重剪";
            return LineChangeKind.DurationChanged;
        }
        basis = "";
        return null;
    }

    private static bool NullDiff(double? a, double? b) =>
        (a ?? 0) != (b ?? 0) || a.HasValue != b.HasValue;

    public static LineDisposition SuggestFor(LineChangeKind kind, bool hasTakes) => kind switch
    {
        LineChangeKind.TextTweak => LineDisposition.NeedsRerecord,
        LineChangeKind.TextAndDuration => LineDisposition.NeedsRerecord,
        LineChangeKind.DurationChanged => LineDisposition.ReeditOnly,
        LineChangeKind.Deleted => LineDisposition.Retire,
        LineChangeKind.Added => LineDisposition.NewRecording,
        LineChangeKind.Split => hasTakes ? LineDisposition.NeedsRerecord : LineDisposition.NewRecording,
        LineChangeKind.Merged => hasTakes ? LineDisposition.ReeditOnly : LineDisposition.NewRecording,
        _ => LineDisposition.NeedsRerecord
    };

    private RevisionAnalysisReport MakeReport(RevisionPackage package, List<RevisionChange> changes,
        int textTweak, int dur, int both, int deleted, int added, int split, int merged)
    {
        return new RevisionAnalysisReport(package.Id, package.Code, package.TimelineShiftMs,
            textTweak, dur, both, deleted, added, split, merged,
            changes.Count(c => c.SuggestedDisposition == LineDisposition.NeedsRerecord),
            changes.Count(c => c.SuggestedDisposition == LineDisposition.ReeditOnly),
            changes.Count(c => c.SuggestedDisposition == LineDisposition.Keep),
            changes.Count(c => c.SuggestedDisposition == LineDisposition.Retire),
            changes.Count(c => c.SuggestedDisposition == LineDisposition.NewRecording),
            changes.OrderBy(c => c.Id).ToList());
    }

    private int NextOrdinal(int projectId) =>
        (_db.Conn.Table<RevisionPackage>().Where(p => p.ProjectId == projectId)
             .Select(p => p.Ordinal).DefaultIfEmpty(0).Max()) + 1;
}
