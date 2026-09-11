using DubStudio.Core.Data;
using DubStudio.Core.Models;

namespace DubStudio.Core.Services;

public sealed record BatchDecision(int Applied, int Skipped, string Basis);

/// <summary>修订包操作：拆分/合并、声音导演逐条与批量处置、确认生效、撤回。</summary>
public sealed partial class RevisionPackageService
{
    public RevisionPackage RequirePackage(int packageId, RevisionState? state = null)
    {
        var p = _db.Conn.Find<RevisionPackage>(packageId)
            ?? throw new InvalidOperationException("修订包不存在");
        if (state != null && p.State != state)
            throw new InvalidOperationException($"修订包 {p.Code} 当前状态 {p.State}，该操作要求 {state}");
        return p;
    }

    public List<RevisionChange> Changes(int packageId) =>
        _db.Conn.Table<RevisionChange>().Where(c => c.PackageId == packageId)
            .OrderBy(c => c.Id).ToList();

    public RevisionAnalysisReport GetReport(int packageId)
    {
        var p = RequirePackage(packageId);
        var changes = Changes(packageId);
        return new RevisionAnalysisReport(p.Id, p.Code, p.TimelineShiftMs,
            changes.Count(c => c.Kind == LineChangeKind.TextTweak),
            changes.Count(c => c.Kind == LineChangeKind.DurationChanged),
            changes.Count(c => c.Kind == LineChangeKind.TextAndDuration),
            changes.Count(c => c.Kind == LineChangeKind.Deleted),
            changes.Count(c => c.Kind == LineChangeKind.Added),
            changes.Count(c => c.Kind == LineChangeKind.Split),
            changes.Count(c => c.Kind == LineChangeKind.Merged),
            changes.Count(c => c.SuggestedDisposition == LineDisposition.NeedsRerecord),
            changes.Count(c => c.SuggestedDisposition == LineDisposition.ReeditOnly),
            changes.Count(c => c.SuggestedDisposition == LineDisposition.Keep),
            changes.Count(c => c.SuggestedDisposition == LineDisposition.Retire),
            changes.Count(c => c.SuggestedDisposition == LineDisposition.NewRecording),
            changes);
    }

    // —— 语句拆分 ——

    /// <summary>把一句拆成多句：源句保留为第一片段（需补录），其余片段为新增句（新补录）。</summary>
    public List<RevisionChange> SplitLine(int packageId, int sourceLineId, IReadOnlyList<SplitPart> parts)
    {
        var pkg = RequirePackage(packageId, RevisionState.Draft);
        if (parts == null || parts.Count < 2) throw new ArgumentException("拆分至少需要两个片段");
        if (_db.Conn.Table<RevisionChange>().Any(c => c.PackageId == packageId && c.LineId == sourceLineId))
            throw new InvalidOperationException("该句已在本修订包内存在变更，不能再拆分");
        var source = _db.Conn.Find<ScriptLine>(sourceLineId)
            ?? throw new InvalidOperationException("源台词不存在");

        var result = new List<RevisionChange>();
        var takes = _db.Conn.Table<Take>().Where(t => t.LineId == sourceLineId).ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (string.IsNullOrWhiteSpace(part.OriginalText))
                throw new ArgumentException($"第 {i + 1} 个片段原文为空");
            var ch = new RevisionChange
            {
                PackageId = packageId,
                LineId = i == 0 ? sourceLineId : 0,
                Kind = LineChangeKind.Split,
                SourceLineIds = i == 0 ? "" : sourceLineId.ToString(),
                NewOriginalText = part.OriginalText,
                NewTranslatedText = part.TranslatedText,
                NewInPointMs = part.InPointMs,
                NewMouthCloseMs = part.MouthCloseMs,
                NewAccentMs = part.AccentMs,
                NewMaxDurationMs = part.MaxDurationMs,
                TargetCharacterName = CharacterName(source.CharacterId),
                AffectedTakeIds = i == 0 ? Csv(takes.Select(t => t.Id)) : ""
            };
            if (i == 0)
            {
                ch.SuggestedDisposition = takes.Count > 0
                    ? LineDisposition.NeedsRerecord : LineDisposition.NewRecording;
                ch.SuggestionBasis = $"语句拆分：源句 {source.Code} 保留为第 1/{parts.Count} 片段，旧录音不完整，需补录";
                SnapshotLine(ch, source, snapshotText: true);
            }
            else
            {
                ch.SuggestedDisposition = LineDisposition.NewRecording;
                ch.SuggestionBasis = $"语句拆分：由 {source.Code} 拆出的第 {i + 1}/{parts.Count} 片段，等待补录";
                ch.TargetSceneKey = _db.Conn.Find<Scene>(source.SceneId)?.StableKey;
            }
            _db.Conn.Insert(ch);
            result.Add(ch);
        }
        return result;
    }

    // —— 语句合并 ——

    /// <summary>把多句合并为目标句：目标句承载合并文本（仅需重剪/核对），其余源句按删句吸收。</summary>
    public List<RevisionChange> MergeLines(int packageId, int targetLineId, IReadOnlyList<int> sourceLineIds,
        string mergedOriginal, string? mergedTranslated = null,
        double? inPointMs = null, double? mouthCloseMs = null,
        double? accentMs = null, double? maxDurationMs = null)
    {
        var pkg = RequirePackage(packageId, RevisionState.Draft);
        if (string.IsNullOrWhiteSpace(mergedOriginal)) throw new ArgumentException("合并文本为空");
        var sources = sourceLineIds.Distinct().Where(id => id != targetLineId).ToList();
        if (sources.Count < 1) throw new ArgumentException("合并至少需要目标句之外的一个源句");

        var allIds = sources.Prepend(targetLineId).ToList();
        foreach (var id in allIds)
        {
            if (_db.Conn.Find<ScriptLine>(id) == null) throw new InvalidOperationException($"台词 {id} 不存在");
            if (_db.Conn.Table<RevisionChange>().Any(c => c.PackageId == packageId && c.LineId == id))
                throw new InvalidOperationException($"台词 {id} 已在本修订包内存在变更，不能参与合并");
        }

        var result = new List<RevisionChange>();
        var target = _db.Conn.Find<ScriptLine>(targetLineId)!;
        var targetTakes = _db.Conn.Table<Take>().Where(t => t.LineId == targetLineId).ToList();
        var targetChange = new RevisionChange
        {
            PackageId = packageId,
            LineId = targetLineId,
            Kind = LineChangeKind.Merged,
            SourceLineIds = Csv(sources),
            NewOriginalText = mergedOriginal,
            NewTranslatedText = mergedTranslated,
            NewInPointMs = inPointMs,
            NewMouthCloseMs = mouthCloseMs,
            NewAccentMs = accentMs,
            NewMaxDurationMs = maxDurationMs,
            SuggestedDisposition = targetTakes.Count > 0 ? LineDisposition.ReeditOnly : LineDisposition.NewRecording,
            SuggestionBasis = $"语句合并：{sources.Count + 1} 句并入 {target.Code}，文字已合并，原录音需重剪核对",
            AffectedTakeIds = Csv(targetTakes.Select(t => t.Id))
        };
        SnapshotLine(targetChange, target, snapshotText: true);
        _db.Conn.Insert(targetChange);
        result.Add(targetChange);

        foreach (var sid in sources.OrderBy(x => x))
        {
            var src = _db.Conn.Find<ScriptLine>(sid)!;
            var srcTakes = _db.Conn.Table<Take>().Where(t => t.LineId == sid).ToList();
            var absorbed = new RevisionChange
            {
                PackageId = packageId,
                LineId = sid,
                Kind = LineChangeKind.Deleted,
                SourceLineIds = targetLineId.ToString(),
                SuggestedDisposition = LineDisposition.Retire,
                SuggestionBasis = $"语句合并：该句并入 {target.Code}，按删句退役（历史录音保留，不混入新批次）",
                AffectedTakeIds = Csv(srcTakes.Select(t => t.Id))
            };
            SnapshotLine(absorbed, src, snapshotText: false);
            _db.Conn.Insert(absorbed);
            result.Add(absorbed);
        }
        return result;
    }

    // —— 声音导演处置 ——

    private static readonly Dictionary<LineChangeKind, HashSet<LineDisposition>> Allowed = new()
    {
        [LineChangeKind.TextTweak] = new() { LineDisposition.NeedsRerecord, LineDisposition.ReeditOnly, LineDisposition.Keep },
        [LineChangeKind.TextAndDuration] = new() { LineDisposition.NeedsRerecord, LineDisposition.ReeditOnly, LineDisposition.Keep },
        [LineChangeKind.DurationChanged] = new() { LineDisposition.ReeditOnly, LineDisposition.Keep, LineDisposition.NeedsRerecord },
        [LineChangeKind.Deleted] = new() { LineDisposition.Retire, LineDisposition.Keep },
        [LineChangeKind.Added] = new() { LineDisposition.NewRecording },
        [LineChangeKind.Split] = new() { LineDisposition.NeedsRerecord, LineDisposition.NewRecording, LineDisposition.Keep },
        [LineChangeKind.Merged] = new() { LineDisposition.ReeditOnly, LineDisposition.NeedsRerecord, LineDisposition.NewRecording, LineDisposition.Keep },
    };

    /// <summary>声音导演逐条确认处置；与建议不同（或该句无建议）时必须填写保存依据。</summary>
    public RevisionChange SetDisposition(int changeId, LineDisposition disposition, string? basis)
    {
        var ch = _db.Conn.Find<RevisionChange>(changeId)
            ?? throw new InvalidOperationException("变更条目不存在");
        var pkg = RequirePackage(ch.PackageId, RevisionState.Draft);

        if (disposition == LineDisposition.Undecided)
            throw new ArgumentException("请选择具体处置，不能保持未决定");
        if (!Allowed.TryGetValue(ch.Kind, out var set) || !set.Contains(disposition))
            throw new ArgumentException($"{ch.Kind} 类型不允许处置为 {disposition}");

        var reason = basis?.Trim() ?? "";
        var overrideSuggestion = disposition != ch.SuggestedDisposition;
        if (overrideSuggestion && reason.Length == 0)
            throw new ArgumentException("处置与系统建议不同，必须填写保存依据（声音导演判断理由）");

        ch.Disposition = disposition;
        ch.DecisionBasis = reason.Length > 0
            ? reason
            : $"采纳系统建议（{ch.SuggestedDisposition}）：{ch.SuggestionBasis}";
        ch.DecidedAt = ReviewWorkflow.Now();
        _db.Conn.Update(ch);
        return ch;
    }

    /// <summary>批量判断：把所有仍未决定的条目按系统建议圈定，并为整批保存同一条依据。</summary>
    public BatchDecision ApplySuggestedDispositions(int packageId, string basis, IEnumerable<LineChangeKind>? onlyKinds = null)
    {
        var pkg = RequirePackage(packageId, RevisionState.Draft);
        if (string.IsNullOrWhiteSpace(basis))
            throw new ArgumentException("批量判断必须保存依据");
        var filter = onlyKinds?.ToHashSet();
        var applied = 0;
        foreach (var ch in Changes(packageId)
                     .Where(c => c.Disposition == LineDisposition.Undecided
                                 && (filter == null || filter.Contains(c.Kind))))
        {
            ch.Disposition = ch.SuggestedDisposition;
            ch.DecisionBasis = $"批量：{basis.Trim()}（系统建议 {ch.SuggestedDisposition}：{ch.SuggestionBasis}）";
            ch.DecidedAt = ReviewWorkflow.Now();
            _db.Conn.Update(ch);
            applied++;
        }
        return new BatchDecision(applied, Changes(packageId).Count(c => c.Disposition == LineDisposition.Undecided), basis);
    }

    /// <summary>确认修订包：所有条目处置完毕后，把处置落实到在制数据。</summary>
    public RevisionPackage ConfirmPackage(int packageId)
    {
        var pkg = RequirePackage(packageId, RevisionState.Draft);
        var changes = Changes(packageId);
        if (changes.Count == 0 && pkg.TimelineShiftMs == 0)
            throw new InvalidOperationException("修订包没有任何变更");
        var pending = changes.Where(c => c.Disposition == LineDisposition.Undecided).ToList();
        if (pending.Count > 0)
            throw new InvalidOperationException($"仍有 {pending.Count} 条未确认处置，声音导演需逐条/批量判断后再确认");

        _db.InTransaction(() =>
        {
            foreach (var ch in changes.OrderBy(c => c.Id)) ApplyChange(pkg, ch);
            if (pkg.TimelineShiftMs != 0) ApplyTimelineShift(pkg);
            pkg.State = RevisionState.Confirmed;
            pkg.ConfirmedAt = ReviewWorkflow.Now();
            _db.Conn.Update(pkg);
            Touch(pkg.ProjectId);
        });
        return pkg;
    }

    private void ApplyChange(RevisionPackage pkg, RevisionChange ch)
    {
        switch (ch.Kind)
        {
            case LineChangeKind.Added:
                ApplyAdded(pkg, ch);
                break;
            case LineChangeKind.Deleted:
                ApplyDeleted(pkg, ch);
                break;
            case LineChangeKind.Split:
                ApplySplit(pkg, ch);
                break;
            case LineChangeKind.Merged:
                ApplyMerged(pkg, ch);
                break;
            default:
                ApplyModified(pkg, ch);
                break;
        }
    }

    private void ApplyModified(RevisionPackage pkg, RevisionChange ch)
    {
        var line = _db.Conn.Find<ScriptLine>(ch.LineId)
            ?? throw new InvalidOperationException($"变更引用的台词 {ch.LineId} 已不存在");

        if (ch.Kind is LineChangeKind.TextTweak or LineChangeKind.TextAndDuration
            && ch.NewTranslatedText != null
            && StableIds.Normalize(ch.NewTranslatedText) != StableIds.Normalize(line.TranslatedText))
        {
            new LineRevisionService(_db).ReviseText(line.Id, ch.NewTranslatedText!,
                $"修订包 {pkg.Code}：{ch.DecisionBasis}");
            line = _db.Conn.Find<ScriptLine>(line.Id)!;
        }
        if (ch.NewOriginalText != null
            && StableIds.Normalize(ch.NewOriginalText) != StableIds.Normalize(line.OriginalText)
            && ch.Kind != LineChangeKind.DurationChanged)
        {
            line.OriginalText = ch.NewOriginalText;
        }
        ApplyTiming(line, ch);
        line.LipSyncDirty = ch.Disposition is LineDisposition.NeedsRerecord or LineDisposition.ReeditOnly;
        _db.Conn.Update(line);

        ApplyDispositionEffects(pkg, ch, line.Id,
            ch.Kind is LineChangeKind.TextTweak or LineChangeKind.TextAndDuration
                ? ReviewKind.TextChanged : ReviewKind.ShiftedLine);
    }

    private void ApplyAdded(RevisionPackage pkg, RevisionChange ch)
    {
        var projectId = pkg.ProjectId;
        var epCode = ResolveEpisode(null, projectId)?.Code ?? "E01";
        var (scene, episode) = ResolveOrCreateScene(projectId, ch.TargetSceneKey, epCode);
        var character = ResolveOrCreateCharacter(projectId, ch.TargetCharacterName ?? "未命名",
            ch.TargetQualifier);

        var seq = _db.Conn.Table<ScriptLine>().Where(l => l.SceneId == scene.Id).Count() + 1;
        var slot = _db.Conn.Table<ScriptLine>()
            .Count(l => l.SceneId == scene.Id && l.CharacterId == character.Id) + 1;
        var occ = CountExistingOccurrence(scene.Id, character.Id, ch.NewOriginalText ?? "") + 1;
        var line = new ScriptLine
        {
            ProjectId = projectId,
            SceneId = scene.Id,
            CharacterId = character.Id,
            StableKey = StableIds.LineKey(episode.Code, scene.Code, character.StableCode,
                ch.NewOriginalText ?? "", occ),
            Code = StableIds.LineCode(episode.Code, scene.Code, character.StableCode, slot),
            SequenceInScene = seq,
            CharacterSlot = slot,
            Occurrence = occ,
            OriginalText = ch.NewOriginalText ?? "",
            TranslatedText = ch.NewTranslatedText ?? "",
            State = LineState.Active
        };
        ApplyTiming(line, ch);
        _db.Conn.Insert(line);
        var version = new LineTextVersion
        {
            LineId = line.Id,
            RevisionNo = 1,
            Text = line.TranslatedText,
            CreatedAt = ReviewWorkflow.Now(),
            Note = $"修订包 {pkg.Code} 新增句"
        };
        _db.Conn.Insert(version);
        line.CurrentVersionId = version.Id;
        _db.Conn.Update(line);
        // 新增句无既有身份：LineId 保持 0，仅以 TargetLineId 指向新句（撤回据此清理）
        ch.TargetLineId = line.Id;
        _db.Conn.Update(ch);
        ReviewWorkflow.QueueReview(_db.Conn, line.Id, null, ReviewKind.RevisionPackage,
            $"[{pkg.Code}] 新增句，等待补录：{ch.DecisionBasis}", pkg.Id);
    }

    private void ApplySplit(RevisionPackage pkg, RevisionChange ch)
    {
        // 新片段（LineId=0，仅有 TargetLineId）已生成则跳过；源句首片段每次都按当前值更新
        if (ch.LineId == 0 && ch.TargetLineId != null) return;
        if (ch.LineId > 0)
        {
            // 源句 = 第一片段
            var source = _db.Conn.Find<ScriptLine>(ch.LineId)!;
            if (ch.NewOriginalText != null) source.OriginalText = ch.NewOriginalText;
            if (ch.NewTranslatedText != null
                && StableIds.Normalize(ch.NewTranslatedText) != StableIds.Normalize(source.TranslatedText))
            {
                new LineRevisionService(_db).ReviseText(source.Id, ch.NewTranslatedText!,
                    $"修订包 {pkg.Code} 拆分首段：{ch.DecisionBasis}");
                source = _db.Conn.Find<ScriptLine>(source.Id)!;
            }
            ApplyTiming(source, ch);
            source.LipSyncDirty = true;
            _db.Conn.Update(source);
            WriteGenealogy(pkg.Id, GenealogyKind.Split, source.Id, source.Id);
            ApplyDispositionEffects(pkg, ch, source.Id, ReviewKind.TextChanged);
        }
        else
        {
            var sourceId = ParseIds(ch.SourceLineIds).FirstOrDefault();
            var src = _db.Conn.Find<ScriptLine>(sourceId)
                ?? throw new InvalidOperationException("拆分源句不存在");
            var seq = _db.Conn.Table<ScriptLine>().Where(l => l.SceneId == src.SceneId).Count() + 1;
            var slot = _db.Conn.Table<ScriptLine>()
                .Count(l => l.SceneId == src.SceneId && l.CharacterId == src.CharacterId) + 1;
            var occ = CountExistingOccurrence(src.SceneId, src.CharacterId, ch.NewOriginalText ?? "") + 1;
            var scene = _db.Conn.Find<Scene>(src.SceneId)!;
            var ep = _db.Conn.Find<Episode>(scene.EpisodeId)!;
            var character = _db.Conn.Find<Character>(src.CharacterId)!;
            var newLine = new ScriptLine
            {
                ProjectId = pkg.ProjectId,
                SceneId = src.SceneId,
                CharacterId = src.CharacterId,
                StableKey = StableIds.LineKey(ep.Code, scene.Code, character.StableCode,
                    ch.NewOriginalText ?? "", occ),
                Code = StableIds.LineCode(ep.Code, scene.Code, character.StableCode, slot),
                SequenceInScene = seq,
                CharacterSlot = slot,
                Occurrence = occ,
                OriginalText = ch.NewOriginalText ?? "",
                TranslatedText = ch.NewTranslatedText ?? "",
                State = LineState.Active,
                LipSyncDirty = true
            };
            ApplyTiming(newLine, ch);
            _db.Conn.Insert(newLine);
            var version = new LineTextVersion
            {
                LineId = newLine.Id,
                RevisionNo = 1,
                Text = newLine.TranslatedText,
                CreatedAt = ReviewWorkflow.Now(),
                Note = $"修订包 {pkg.Code} 拆出新句"
            };
            _db.Conn.Insert(version);
            newLine.CurrentVersionId = version.Id;
            _db.Conn.Update(newLine);
            // 新片段保留 LineId=0（无既有身份），仅以 TargetLineId 指向新句，撤回时据此清理
            ch.TargetLineId = newLine.Id;
            _db.Conn.Update(ch);
            WriteGenealogy(pkg.Id, GenealogyKind.Split, sourceId, newLine.Id);
            ReviewWorkflow.QueueReview(_db.Conn, newLine.Id, null, ReviewKind.RevisionPackage,
                $"[{pkg.Code}] 拆出新句，等待补录：{ch.DecisionBasis}", pkg.Id);
        }
    }

    private void ApplyMerged(RevisionPackage pkg, RevisionChange ch)
    {
        var target = _db.Conn.Find<ScriptLine>(ch.LineId)
            ?? throw new InvalidOperationException("合并目标句不存在");
        if (ch.NewOriginalText != null
            && StableIds.Normalize(ch.NewOriginalText) != StableIds.Normalize(target.OriginalText))
            target.OriginalText = ch.NewOriginalText;
        if (ch.NewTranslatedText != null
            && StableIds.Normalize(ch.NewTranslatedText) != StableIds.Normalize(target.TranslatedText))
        {
            new LineRevisionService(_db).ReviseText(target.Id, ch.NewTranslatedText!,
                $"修订包 {pkg.Code} 合并：{ch.DecisionBasis}");
            target = _db.Conn.Find<ScriptLine>(target.Id)!;
        }
        ApplyTiming(target, ch);
        target.LipSyncDirty = ch.Disposition != LineDisposition.Keep;
        // ReviseText 可能已更新过该行并被重新取出，原文改动手动补回，避免被旧快照覆盖
        if (ch.NewOriginalText != null
            && StableIds.Normalize(ch.NewOriginalText) != StableIds.Normalize(target.OriginalText))
            target.OriginalText = ch.NewOriginalText;
        _db.Conn.Update(target);
        foreach (var sid in ParseIds(ch.SourceLineIds))
            WriteGenealogy(pkg.Id, GenealogyKind.Merge, sid, target.Id);
        ApplyDispositionEffects(pkg, ch, target.Id, ReviewKind.TextChanged);
    }

    private void ApplyDeleted(RevisionPackage pkg, RevisionChange ch)
    {
        var line = _db.Conn.Find<ScriptLine>(ch.LineId);
        if (line == null) return;

        if (ch.Disposition == LineDisposition.Keep)
        {
            // 导演推翻删句建议：保留该句，仅记录一次重剪提示
            ReviewWorkflow.QueueReview(_db.Conn, line.Id, null, ReviewKind.RevisionPackage,
                $"[{pkg.Code}] 新稿删除但导演判定保留：{ch.DecisionBasis}", pkg.Id);
            return;
        }

        line.State = LineState.Retired;
        line.LipSyncDirty = true;
        _db.Conn.Update(line);
        var pick = _db.Conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == line.Id);
        if (pick != null)
        {
            pick.Status = PickStatus.PendingReview;
            pick.UpdatedAt = ReviewWorkflow.Now();
            _db.Conn.Update(pick);
        }
        foreach (var tid in ParseIds(ch.AffectedTakeIds))
        {
            var take = _db.Conn.Find<Take>(tid);
            if (take != null && take.Status == TakeStatus.Usable)
            {
                take.Status = TakeStatus.NeedsReview;
                _db.Conn.Update(take);
            }
            ReviewWorkflow.QueueReview(_db.Conn, line.Id, tid, ReviewKind.RevisionPackage,
                $"[{pkg.Code}] 删句退役，录音保留但不再交付：{ch.DecisionBasis}", pkg.Id);
        }
        if (ParseIds(ch.AffectedTakeIds).Count == 0)
            ReviewWorkflow.QueueReview(_db.Conn, line.Id, null, ReviewKind.RevisionPackage,
                $"[{pkg.Code}] 删句退役：{ch.DecisionBasis}", pkg.Id);
    }

    /// <summary>按导演处置落实条次/选用状态，并保存处置依据到复核消息。</summary>
    private void ApplyDispositionEffects(RevisionPackage pkg, RevisionChange ch, int lineId, ReviewKind kind)
    {
        var takeIds = ParseIds(ch.AffectedTakeIds);
        switch (ch.Disposition)
        {
            case LineDisposition.NeedsRerecord or LineDisposition.NewRecording:
                ReviewWorkflow.InvalidateForPackage(_db.Conn, lineId, kind,
                    $"需补录：{ch.DecisionBasis}", ch.Disposition == LineDisposition.NeedsRerecord, pkg.Id);
                break;
            case LineDisposition.ReeditOnly:
                foreach (var tid in takeIds)
                    ReviewWorkflow.QueueReview(_db.Conn, lineId, tid, ReviewKind.RevisionPackage,
                        $"仅需重剪（录音保留，按新时长/时间轴重剪）：{ch.DecisionBasis}", pkg.Id);
                var pick = _db.Conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == lineId);
                if (pick != null)
                    ReviewWorkflow.QueueReview(_db.Conn, lineId, pick.TakeId, ReviewKind.RevisionPackage,
                        "选用条次重剪后由导演复核", pkg.Id);
                break;
            case LineDisposition.Keep:
                // 可以保留：关闭本句由本包产生的待处理影响，不动音频
                foreach (var r in _db.Conn.Table<ReviewItem>()
                             .Where(r => r.LineId == lineId && r.PackageId == pkg.Id && !r.Resolved).ToList())
                    ReviewWorkflow.Resolve(_db.Conn, r.Id);
                break;
        }
    }

    private void ApplyTiming(ScriptLine line, RevisionChange ch)
    {
        if (ch.NewInPointMs.HasValue) line.InPointMs = ch.NewInPointMs;
        if (ch.NewMouthCloseMs.HasValue) line.MouthCloseMs = ch.NewMouthCloseMs;
        if (ch.NewAccentMs.HasValue) line.AccentMs = ch.NewAccentMs;
        if (ch.NewMaxDurationMs.HasValue) line.MaxDurationMs = ch.NewMaxDurationMs;
        if (line.MouthCloseMs.HasValue && line.InPointMs.HasValue && line.MouthCloseMs < line.InPointMs)
            throw new InvalidOperationException($"句 {line.Code} 闭口点早于入点，拒绝确认");
    }

    private void ApplyTimelineShift(RevisionPackage pkg)
    {
        var changeByLine = Changes(pkg.Id).ToDictionary(c => c.LineId);
        foreach (var id in ParseIds(pkg.ShiftLineIds))
        {
            var line = _db.Conn.Find<ScriptLine>(id);
            if (line == null) continue;
            var delta = pkg.TimelineShiftMs;
            line.InPointMs = Add(line.InPointMs, delta);
            line.MouthCloseMs = Add(line.MouthCloseMs, delta);
            line.AccentMs = Add(line.AccentMs, delta);
            _db.Conn.Update(line);
            changeByLine.TryGetValue(id, out var shiftChange);
            var keep = shiftChange?.Disposition == LineDisposition.Keep;
            foreach (var t in _db.Conn.Table<Take>().Where(t => t.LineId == id).ToList())
            {
                t.StartMs = Math.Max(0, t.StartMs + delta);
                t.EndMs = Math.Max(0, t.EndMs + delta);
                _db.Conn.Update(t);
                if (!keep)
                    ReviewWorkflow.QueueReview(_db.Conn, id, t.Id, ReviewKind.ShiftedLine,
                        $"时间轴整体平移 {delta:0}ms，仅需重剪对齐", pkg.Id);
            }
        }
    }

    private void WriteGenealogy(int packageId, GenealogyKind kind, int sourceId, int targetId)
    {
        if (_db.Conn.Table<LineGenealogy>().Any(g =>
                g.PackageId == packageId && g.Kind == kind && g.SourceLineId == sourceId
                && g.TargetLineId == targetId))
            return;
        _db.Conn.Insert(new LineGenealogy
        {
            PackageId = packageId,
            Kind = kind,
            SourceLineId = sourceId,
            TargetLineId = targetId
        });
    }

    // —— 撤回 ——

    /// <summary>撤回修订包：已确认/已交付均可撤回（交付物目录保留可追溯），在制数据按快照回退。</summary>
    public RevisionPackage WithdrawPackage(int packageId, string? reason = null)
    {
        var pkg = RequirePackage(packageId);
        if (pkg.State == RevisionState.Withdrawn)
            throw new InvalidOperationException("修订包已撤回");
        if (pkg.State == RevisionState.Draft)
        {
            // 草案撤回：仅删除分析条目，无在制改动
            ReviewWorkflow.WithdrawPackageReviews(_db.Conn, packageId);
            foreach (var ch in Changes(packageId)) _db.Conn.Delete(ch);
            pkg.State = RevisionState.Withdrawn;
            pkg.WithdrawnAt = ReviewWorkflow.Now();
            pkg.Reason = AppendReason(pkg.Reason, reason);
            _db.Conn.Update(pkg);
            return pkg;
        }

        _db.InTransaction(() =>
        {
            // 逆序回退（新增句先生成的 id 较小，逆序保证拆分新句先清理）
            foreach (var ch in Changes(packageId).OrderByDescending(c => c.Id)) RollbackChange(pkg, ch);
            if (pkg.TimelineShiftMs != 0) RollbackTimelineShift(pkg);
            foreach (var g in _db.Conn.Table<LineGenealogy>().Where(g => g.PackageId == packageId).ToList())
                _db.Conn.Delete(g);
            ReviewWorkflow.WithdrawPackageReviews(_db.Conn, packageId);
            pkg.State = RevisionState.Withdrawn;
            pkg.WithdrawnAt = ReviewWorkflow.Now();
            pkg.Reason = AppendReason(pkg.Reason, reason);
            _db.Conn.Update(pkg);
            Touch(pkg.ProjectId);
        });
        return pkg;
    }

    private void RollbackChange(RevisionPackage pkg, RevisionChange ch)
    {
        switch (ch.Kind)
        {
            case LineChangeKind.Added:
                RollbackAdded(ch);
                break;
            case LineChangeKind.Split:
                // 拆出的新片段 LineId 保持 0、仅有 TargetLineId → 按新增句清理
                if (ch.LineId == 0 && ch.TargetLineId != null)
                    RollbackAdded(ch);
                else
                    RestoreLine(ch);
                break;
            case LineChangeKind.Merged:
            case LineChangeKind.Deleted:
            default:
                RestoreLine(ch);
                break;
        }
        RestoreTakesAndPick(ch);
    }

    private void RollbackAdded(RevisionChange ch)
    {
        var lineId = ch.LineId > 0 ? ch.LineId : ch.TargetLineId;
        if (lineId == null) return;
        var line = _db.Conn.Find<ScriptLine>(lineId.Value);
        if (line == null) return;
        var takes = _db.Conn.Table<Take>().Where(t => t.LineId == lineId.Value).ToList();
        if (takes.Count > 0)
        {
            // 撤回前已在该新句上补录：不删除录音，保留为退役句并入复核
            line.State = LineState.Retired;
            _db.Conn.Update(line);
            ReviewWorkflow.QueueReview(_db.Conn, line.Id, null, ReviewKind.RevisionPackage,
                "修订包撤回：新增句已有补录，保留录音但该句退役待人工处理");
        }
        else
        {
            foreach (var v in _db.Conn.Table<LineTextVersion>().Where(v => v.LineId == lineId.Value).ToList())
                _db.Conn.Delete(v);
            _db.Conn.Delete(line);
        }
    }

    private void RestoreLine(RevisionChange ch)
    {
        if (ch.LineId == 0) return;
        var line = _db.Conn.Find<ScriptLine>(ch.LineId);
        if (line == null) return;
        line.State = ch.SnapshotLineState;
        if (ch.SnapshotTranslatedText != null) line.TranslatedText = ch.SnapshotTranslatedText;
        if (ch.SnapshotVersionId is > 0) line.CurrentVersionId = ch.SnapshotVersionId.Value;
        line.InPointMs = ch.SnapshotInPointMs;
        line.MouthCloseMs = ch.SnapshotMouthCloseMs;
        line.AccentMs = ch.SnapshotAccentMs;
        line.MaxDurationMs = ch.SnapshotMaxDurationMs;
        _db.Conn.Update(line);
    }

    private void RestoreTakesAndPick(RevisionChange ch)
    {
        // 按确认前快照精确恢复条次状态（随后本包复核项统一回收）
        var ids = ParseIds(ch.AffectedTakeIds);
        var statuses = ch.SnapshotTakeStatuses
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Enum.Parse<TakeStatus>(s)).ToList();
        for (var i = 0; i < ids.Count; i++)
        {
            var take = _db.Conn.Find<Take>(ids[i]);
            if (take == null) continue;
            take.Status = i < statuses.Count ? statuses[i] : TakeStatus.Usable;
            _db.Conn.Update(take);
        }
        var pick = _db.Conn.Table<DirectorPick>().FirstOrDefault(p => p.LineId == ch.LineId);
        if (pick != null && ch.SnapshotPickTakeId is > 0)
        {
            pick.TakeId = ch.SnapshotPickTakeId.Value;
            pick.Status = ch.SnapshotPickStatus;
            pick.Note = ch.SnapshotPickNote;
            pick.UpdatedAt = ReviewWorkflow.Now();
            _db.Conn.Update(pick);
        }
    }

    private void RollbackTimelineShift(RevisionPackage pkg)
    {
        var delta = -pkg.TimelineShiftMs;
        foreach (var id in ParseIds(pkg.ShiftLineIds))
        {
            // 行关键点的平移随对应 DurationChanged 变更行的快照回退，这里只回退条次时间段
            foreach (var t in _db.Conn.Table<Take>().Where(t => t.LineId == id).ToList())
            {
                t.StartMs = Math.Max(0, t.StartMs + delta);
                t.EndMs = Math.Max(0, t.EndMs + delta);
                _db.Conn.Update(t);
            }
        }
    }

    // —— 解析/创建辅助 ——

    private (Scene scene, Episode episode) ResolveOrCreateScene(int projectId, string? sceneKey, string epCode)
    {
        var episode = ResolveEpisode(epCode, projectId)
            ?? _db.Conn.Table<Episode>().FirstOrDefault()
            ?? CreateEpisode(projectId, epCode);
        if (!string.IsNullOrWhiteSpace(sceneKey))
        {
            var existing = _db.Conn.Table<Scene>()
                .FirstOrDefault(s => s.ProjectId == projectId && s.StableKey == sceneKey);
            if (existing != null) return (existing, episode);
        }
        var ordinal = (_db.Conn.Table<Scene>().Where(s => s.EpisodeId == episode.Id)
                           .Select(s => s.Ordinal).DefaultIfEmpty(0).Max()) + 1;
        var code = StableIds.SceneCode(ordinal);
        var scene = new Scene
        {
            ProjectId = projectId,
            EpisodeId = episode.Id,
            Code = code,
            Ordinal = ordinal,
            StableKey = sceneKey ?? $"{episode.Code}|{code}"
        };
        _db.Conn.Insert(scene);
        return (scene, episode);
    }

    private Episode CreateEpisode(int projectId, string code)
    {
        var ordinal = _db.Conn.Table<Episode>().Where(e => e.ProjectId == projectId).Count() + 1;
        var ep = new Episode { ProjectId = projectId, Code = code, Ordinal = ordinal, StableKey = code };
        _db.Conn.Insert(ep);
        return ep;
    }

    private Character ResolveOrCreateCharacter(int projectId, string displayName, string? qualifier)
    {
        var code = StableIds.CharacterCode(displayName, qualifier);
        var existing = _db.Conn.Table<Character>()
            .FirstOrDefault(c => c.ProjectId == projectId && c.StableCode == code);
        if (existing != null) return existing;
        var ordinal = _db.Conn.Table<Character>().Count(c => c.ProjectId == projectId && c.DisplayName == displayName) + 1;
        var character = new Character
        {
            ProjectId = projectId,
            DisplayName = displayName,
            Qualifier = string.IsNullOrWhiteSpace(qualifier) ? null : qualifier,
            StableCode = code,
            NameOrdinal = ordinal
        };
        _db.Conn.Insert(character);
        return character;
    }

    private int CountExistingOccurrence(int sceneId, int characterId, string originalText)
    {
        var hash = StableIds.TextHash(originalText);
        return _db.Conn.Table<ScriptLine>()
            .Count(l => l.SceneId == sceneId && l.CharacterId == characterId
                && l.StableKey.EndsWith(hash));
    }

    private string CharacterName(int characterId) =>
        _db.Conn.Find<Character>(characterId)?.DisplayName ?? "";

    private static double? Add(double? v, double delta) => v.HasValue ? Math.Max(0, v.Value + delta) : v;

    private static string AppendReason(string oldReason, string? add) =>
        string.IsNullOrWhiteSpace(add) ? oldReason : $"{oldReason}｜撤回：{add.Trim()}";

    private void Touch(int projectId)
    {
        var p = _db.Conn.Find<Project>(projectId);
        if (p != null) { p.UpdatedAt = ReviewWorkflow.Now(); _db.Conn.Update(p); }
    }
}
