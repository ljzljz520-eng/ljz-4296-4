using DubStudio.Core.Data;
using DubStudio.Core.Import;
using DubStudio.Core.Models;
using DubStudio.Core.Timecode;

namespace DubStudio.Core.Services;

public sealed record ImportReport(
    int EpisodesTouched,
    int ScenesCreated,
    int LinesCreated,
    int LinesUpdated,
    int LinesRetired,
    int LinesRevived,
    int ReviewsQueued,
    List<string> Warnings)
{
    public static ImportReport Empty() => new(0, 0, 0, 0, 0, 0, 0, new());
}

/// <summary>剧本导入：以集/场/角色/原语句生成稳定编号；重复导入幂等；
/// 对白移位、换句、再次导入冲突都走待复核而非覆盖式自动适用。</summary>
public sealed class ScriptImportService
{
    private readonly StudioDatabase _db;
    public ScriptImportService(StudioDatabase db) => _db = db;

    public ImportReport Import(ScriptDocument doc, int projectId = 1)
    {
        var warnings = new List<string>();
        int scenesCreated = 0, linesCreated = 0, linesUpdated = 0,
            linesRetired = 0, linesRevived = 0, reviews = 0;
        var touchedLineKeys = new HashSet<string>();
        var touchedEpisodeKeys = new HashSet<string>();

        _db.InTransaction(() =>
        {
            var project = _db.RequireProject(projectId);
            var episodes = _db.Conn.Table<Episode>().Where(e => e.ProjectId == projectId).ToList();
            var scenes = _db.Conn.Table<Scene>().Where(s => s.ProjectId == projectId).ToList();
            var characters = _db.Conn.Table<Character>().Where(c => c.ProjectId == projectId).ToList();
            var lines = _db.Conn.Table<ScriptLine>().Where(l => l.ProjectId == projectId).ToList();

            if (!string.IsNullOrWhiteSpace(doc.FrameRate))
            {
                var fr = FrameRate.Parse(doc.FrameRate);
                project.FrameRateNum = fr.Num;
                project.FrameRateDen = fr.Den;
                project.VariableFrameRate = doc.VariableFrameRate || project.VariableFrameRate;
            }
            else if (doc.VariableFrameRate)
            {
                project.VariableFrameRate = true;
            }
            _db.Conn.Update(project);

            var epOrdinal = 0;
            foreach (var sdoc in doc.Scenes)
            {
                epOrdinal++;
                var epCode = !string.IsNullOrWhiteSpace(doc.EpisodeCode)
                    ? doc.EpisodeCode!.Trim()
                    : StableIds.EpisodeCode(epOrdinal);
                var epKey = epCode;
                var episode = episodes.FirstOrDefault(e => e.StableKey == epKey);
                if (episode == null)
                {
                    episode = new Episode
                    {
                        ProjectId = projectId,
                        Code = epCode,
                        Ordinal = episodes.Count == 0 ? epOrdinal : episodes.Max(e => e.Ordinal) + 1,
                        Title = doc.EpisodeTitle,
                        StableKey = epKey
                    };
                    _db.Conn.Insert(episode);
                    episodes.Add(episode);
                }
                else if (doc.EpisodeTitle != null && episode.Title != doc.EpisodeTitle)
                {
                    episode.Title = doc.EpisodeTitle;
                    _db.Conn.Update(episode);
                }
                touchedEpisodeKeys.Add(epKey);

                // 稳定场键：显式编号 > 场记标记(slug) > 文档内位置；重复导入同一文档保持稳定
                var docScenePos = doc.Scenes.IndexOf(sdoc) + 1;
                var sceneKey = SceneKey(epCode, sdoc, docScenePos);
                var scene = scenes.FirstOrDefault(x => x.ProjectId == projectId && x.StableKey == sceneKey);
                if (scene == null)
                {
                    var epSceneOrd = scenes.Where(x => x.EpisodeId == episode!.Id).Select(x => x.Ordinal).DefaultIfEmpty().Max() + 1;
                    var sceneCode = !string.IsNullOrWhiteSpace(sdoc.Code)
                        ? sdoc.Code!.Trim()
                        : StableIds.SceneCode(epSceneOrd);
                    scene = new Scene
                    {
                        ProjectId = projectId,
                        EpisodeId = episode!.Id,
                        Code = sceneCode,
                        Ordinal = epSceneOrd,
                        Slug = sdoc.Slug,
                        StableKey = sceneKey
                    };
                    _db.Conn.Insert(scene);
                    scenes.Add(scene);
                    scenesCreated++;
                }

                // 同场同角色出现序号（支持同角多句）
                var slotByCharacter = new Dictionary<string, int>();
                // 同(场,角色,原文)出现计数（重复台词消歧）
                var occurrenceByKey = new Dictionary<string, int>();
                var seq = 0;

                // 已退役且本次重新出现的行，需要恢复并提示复核
                foreach (var ldto in sdoc.Lines)
                {
                    seq++;
                    var charCode = StableIds.CharacterCode(ldto.Character, ldto.Qualifier);
                    var character = characters.FirstOrDefault(c => c.ProjectId == projectId && c.StableCode == charCode);
                    if (character == null)
                    {
                        var ordinal = characters.Count(c => c.DisplayName == ldto.Character.Trim()) + 1;
                        character = new Character
                        {
                            ProjectId = projectId,
                            DisplayName = ldto.Character.Trim(),
                            Qualifier = string.IsNullOrWhiteSpace(ldto.Qualifier) ? null : ldto.Qualifier!.Trim(),
                            StableCode = charCode,
                            NameOrdinal = ordinal
                        };
                        _db.Conn.Insert(character);
                        characters.Add(character);
                        if (ordinal > 1 && string.IsNullOrWhiteSpace(ldto.Qualifier))
                            warnings.Add($"角色“{ldto.Character.Trim()}”重名第 {ordinal} 次出现，建议使用 @限定 区分；当前视为同一角色");
                    }

                    var baseOccKey = $"{scene.StableKey}|{charCode}|{StableIds.TextHash(ldto.Text)}";
                    var occ = occurrenceByKey.TryGetValue(baseOccKey, out var v) ? v + 1 : 1;
                    occurrenceByKey[baseOccKey] = occ;

                    var slot = slotByCharacter.TryGetValue(charCode, out var sv) ? sv + 1 : 1;
                    slotByCharacter[charCode] = slot;

                    var stableKey = StableIds.LineKey(epCode, scene.Code, charCode, ldto.Text, occ);
                    touchedLineKeys.Add(stableKey);

                    var existing = lines.FirstOrDefault(l => l.StableKey == stableKey);
                    if (existing == null)
                    {
                        var line = new ScriptLine
                        {
                            ProjectId = projectId,
                            SceneId = scene.Id,
                            CharacterId = character.Id,
                            StableKey = stableKey,
                            Code = StableIds.LineCode(epCode, scene.Code, charCode, slot),
                            SequenceInScene = seq,
                            CharacterSlot = slot,
                            Occurrence = occ,
                            OriginalText = ldto.Text,
                            TranslatedText = "",
                            State = LineState.Active
                        };
                        _db.Conn.Insert(line);
                        var version = new LineTextVersion
                        {
                            LineId = line.Id,
                            RevisionNo = 1,
                            Text = "",
                            CreatedAt = ReviewWorkflow.Now(),
                            Note = "导入初始版本"
                        };
                        _db.Conn.Insert(version);
                        line.CurrentVersionId = version.Id;
                        _db.Conn.Update(line);
                        lines.Add(line);
                        linesCreated++;
                    }
                    else
                    {
                        bool moved = existing.SceneId != scene.Id || existing.CharacterId != character.Id;
                        bool revived = existing.State == LineState.Retired;
                        existing.SceneId = scene.Id;
                        existing.CharacterId = character.Id;
                        existing.SequenceInScene = seq;
                        existing.CharacterSlot = slot;
                        existing.Occurrence = occ;
                        existing.Code = StableIds.LineCode(epCode, scene.Code, charCode, slot);
                        var normalizedChanged = StableIds.Normalize(existing.OriginalText) != StableIds.Normalize(ldto.Text);
                        existing.OriginalText = ldto.Text;
                        existing.State = LineState.Active;
                        _db.Conn.Update(existing);
                        linesUpdated++;

                        if (revived)
                        {
                            linesRevived++;
                            reviews += ReviewWorkflow.InvalidateLine(_db.Conn, existing.Id,
                                ReviewKind.ReimportConflict, "台词在新剧本中重新出现，旧录音需复核", true);
                        }
                        if (moved)
                        {
                            reviews += ReviewWorkflow.InvalidateLine(_db.Conn, existing.Id,
                                ReviewKind.ShiftedLine, "对白已移位到其他场/角色，旧录音待复核", true);
                        }
                    }
                }
            }

            // 本次导入集内未出现的旧行 -> 退役（换句/删除），旧录音一律待复核
            var touchedEpIds = episodes.Where(e => touchedEpisodeKeys.Contains(e.StableKey)).Select(e => e.Id).ToHashSet();
            var affectedSceneIds = scenes.Where(s => touchedEpIds.Contains(s.EpisodeId)).Select(s => s.Id).ToHashSet();
            foreach (var line in lines.Where(l => affectedSceneIds.Contains(l.SceneId)
                                                  && !touchedLineKeys.Contains(l.StableKey)
                                                  && l.State == LineState.Active))
            {
                line.State = LineState.Retired;
                line.LipSyncDirty = true;
                _db.Conn.Update(line);
                linesRetired++;
                reviews += ReviewWorkflow.InvalidateLine(_db.Conn, line.Id,
                    ReviewKind.ReimportConflict, "重新导入后该句已不存在或被替换，旧录音待复核", false);
            }
        });

        return new ImportReport(touchedEpisodeKeys.Count, scenesCreated, linesCreated,
            linesUpdated, linesRetired, linesRevived, reviews, warnings);
    }

    private static string SceneKey(string epCode, ScriptSceneDto sdoc, int docPosition)
    {
        if (!string.IsNullOrWhiteSpace(sdoc.Code)) return $"{epCode}|{sdoc.Code!.Trim()}";
        if (!string.IsNullOrWhiteSpace(sdoc.Slug)) return $"{epCode}|#|{sdoc.Slug.Trim()}";
        return $"{epCode}|#pos{docPosition}";
    }
}
