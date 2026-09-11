using SQLite;

namespace DubStudio.Core.Models;

public enum LineState { Active = 0, Retired = 1 }
public enum TakeStatus { Usable = 0, NeedsReview = 1, Rejected = 2 }
public enum PickStatus { Confirmed = 0, PendingReview = 1 }
public enum ReviewKind { TextChanged = 0, ShiftedLine = 1, ReimportConflict = 2, ExternalFileChanged = 3, ActorChanged = 4, RevisionPackage = 5 }

// —— 混录后剧本修订（修订包 / 影响分析 / 交付修订包）——

public enum RevisionState { Draft = 0, Confirmed = 1, Delivered = 2, Withdrawn = 3 }

/// <summary>稳定语句身份比对得出的差异类型。拆分/合并由编辑显式操作标记。</summary>
public enum LineChangeKind
{
    /// <summary>文字微调：译文文本变化（时长参数不变），旧录音需补录。</summary>
    TextTweak = 0,
    /// <summary>时长变化：入点/闭口点/长度限制变化（文本不变），仅需重剪。</summary>
    DurationChanged = 1,
    /// <summary>文字与时长同时变化。</summary>
    TextAndDuration = 2,
    /// <summary>新稿中删除的句（稳定身份消失）。</summary>
    Deleted = 3,
    /// <summary>新稿中新增的句。</summary>
    Added = 4,
    /// <summary>拆分：由一句拆出的新句。</summary>
    Split = 5,
    /// <summary>合并：合入目标句；源句单独记 Deleted（合并吸收）。</summary>
    Merged = 6
}

/// <summary>声音导演逐条处置。Undecided 表示尚未确认。</summary>
public enum LineDisposition
{
    Undecided = 0,
    /// <summary>需补录：旧录音不能用，安排重新配音。</summary>
    NeedsRerecord = 1,
    /// <summary>仅需重剪：录音可用，按新时间轴/时长重新剪辑。</summary>
    ReeditOnly = 2,
    /// <summary>可以保留：原录音与选用原样沿用。</summary>
    Keep = 3,
    /// <summary>删句：退役该句，不再交付。</summary>
    Retire = 4,
    /// <summary>新句/拆出句：等待补录。</summary>
    NewRecording = 5
}

/// <summary>稳定语句身份间的血缘关系（拆分/合并可追溯）。</summary>
public enum GenealogyKind { Split = 0, Merge = 1 }

/// <summary>一个译制工程 = 一个 .dsproj SQLite 数据库文件。</summary>
public class Project
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? SourceMediaPath { get; set; }
    public double FrameRateNum { get; set; } = 24000;
    public double FrameRateDen { get; set; } = 1001;
    public bool VariableFrameRate { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public class Episode
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int ProjectId { get; set; }
    /// <summary>稳定集编号，如 E01。</summary>
    public string Code { get; set; } = "";
    public int Ordinal { get; set; }
    public string? Title { get; set; }
    /// <summary>导入时使用的稳定业务键（集代码）。</summary>
    public string StableKey { get; set; } = "";
}

public class Scene
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int EpisodeId { get; set; }
    [Indexed] public int ProjectId { get; set; }
    /// <summary>集内稳定场编号，如 S012。</summary>
    public string Code { get; set; } = "";
    public int Ordinal { get; set; }
    public string? Slug { get; set; }
    public string StableKey { get; set; } = "";
}

public class Character
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int ProjectId { get; set; }
    public string DisplayName { get; set; } = "";
    /// <summary>消歧限定，如“少年”“老年”；为空则同名视为同一角色。</summary>
    public string? Qualifier { get; set; }
    /// <summary>项目内稳定角色码，如 C_田中#少年 或 C_A1B2。</summary>
    public string StableCode { get; set; } = "";
    public int NameOrdinal { get; set; }
}

public class ScriptLine
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int SceneId { get; set; }
    [Indexed] public int ProjectId { get; set; }
    [Indexed] public int CharacterId { get; set; }
    /// <summary>稳定行键：S|E|角色码|原文哈希|同角同场第n次。导入幂等。</summary>
    [Indexed] public string StableKey { get; set; } = "";
    /// <summary>完整编号，如 E01-S012-C_田中-03。</summary>
    public string Code { get; set; } = "";
    /// <summary>同场展示序号（导入顺序）。</summary>
    public int SequenceInScene { get; set; }
    /// <summary>该角色在该场中的第几句（支持同角多句）。</summary>
    public int CharacterSlot { get; set; }
    public int Occurrence { get; set; }
    public string OriginalText { get; set; } = "";
    public string TranslatedText { get; set; } = "";
    public int CurrentVersionId { get; set; }
    public LineState State { get; set; } = LineState.Active;
    public bool LipSyncDirty { get; set; }
    // —— 口型关键点（毫秒，统一以毫秒时间轴存储；VFR 由帧率换算辅助处理）——
    public double? InPointMs { get; set; }
    public double? MouthCloseMs { get; set; }
    public double? AccentMs { get; set; }
    /// <summary>允许的最大配音长度（毫秒）。</summary>
    public double? MaxDurationMs { get; set; }
}

/// <summary>译文每次改动产生一条不可变版本。</summary>
public class LineTextVersion
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int LineId { get; set; }
    public int RevisionNo { get; set; }
    public string Text { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string? Note { get; set; }
}

public class Take
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int LineId { get; set; }
    /// <summary>录音基于的译文版本；与台词当前版本不一致时视为待复核。</summary>
    [Indexed] public int TextVersionId { get; set; }
    public int TakeNo { get; set; }
    public string Actor { get; set; } = "";
    public string Microphone { get; set; } = "";
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    public string? AudioFilePath { get; set; }
    /// <summary>交付音频摘要（SHA-256），用于外部改动检测。</summary>
    public string? FileSha256 { get; set; }
    public long FileSizeBytes { get; set; }
    public string? Note { get; set; }
    public TakeStatus Status { get; set; } = TakeStatus.Usable;
    public string RecordedAt { get; set; } = "";
}

/// <summary>导演选用：每条台词至多一条；只能引用真实存在的条次。</summary>
public class DirectorPick
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed(Unique = true)] public int LineId { get; set; }
    [Indexed] public int TakeId { get; set; }
    public PickStatus Status { get; set; } = PickStatus.Confirmed;
    public string? Note { get; set; }
    public string UpdatedAt { get; set; } = "";
}

/// <summary>待复核队列项。</summary>
public class ReviewItem
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int LineId { get; set; }
    public int? TakeId { get; set; }
    /// <summary>产生该复核项的修订包；撤回修订包时按此精确回收。</summary>
    [Indexed] public int? PackageId { get; set; }
    public ReviewKind Kind { get; set; }
    public string Message { get; set; } = "";
    public bool Resolved { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? ResolvedAt { get; set; }
}

/// <summary>混录后剧本修订包：编辑提交新稿 → 影响分析 → 导演逐条确认 → 交付修订包。
/// 已交付（已交混音）的包不允许原位替换，撤回后用新包重新交付。</summary>
public class RevisionPackage
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int ProjectId { get; set; }
    /// <summary>工程内顺序号，形成 REV-1、REV-2…</summary>
    public int Ordinal { get; set; }
    public string Code { get; set; } = "";
    public string Reason { get; set; } = "";
    public string SubmittedBy { get; set; } = "";
    public RevisionState State { get; set; } = RevisionState.Draft;
    public string CreatedAt { get; set; } = "";
    public string? ConfirmedAt { get; set; }
    public string? DeliveredAt { get; set; }
    public string? WithdrawnAt { get; set; }
    /// <summary>时间轴整体平移量（毫秒，正=向后）。0 表示无平移。</summary>
    public double TimelineShiftMs { get; set; }
    /// <summary>受平移影响的行 Id（CSV，确认时用于落库、撤回时回退）。</summary>
    public string ShiftLineIds { get; set; } = "";
    /// <summary>受平移影响的条次 Id（CSV）。</summary>
    public string ShiftTakeIds { get; set; } = "";
}

/// <summary>修订包内逐条差异：按稳定语句身份区分文字微调/时长变化/删句/新增/拆分/合并，
/// 保存建议处置、导演实际处置与“保存依据”（含批量判断）。</summary>
public class RevisionChange
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int PackageId { get; set; }
    [Indexed] public int LineId { get; set; }
    public LineChangeKind Kind { get; set; }
    /// <summary>关联的新句行 Id（拆分产生）；普通行为空。</summary>
    public int? TargetLineId { get; set; }
    /// <summary>合并源句 Id（CSV，仅 Merged/被吸收的 Deleted 使用）。</summary>
    public string SourceLineIds { get; set; } = "";
    public LineDisposition SuggestedDisposition { get; set; }
    [Indexed] public LineDisposition Disposition { get; set; } = LineDisposition.Undecided;
    /// <summary>建议依据（程序自动分析的理由）。</summary>
    public string SuggestionBasis { get; set; } = "";
    /// <summary>导演确认的处置依据；批量判断时记录“批量：<依据>”。</summary>
    public string DecisionBasis { get; set; } = "";
    public string? DecidedAt { get; set; }
    // —— 新稿值（仅 Changed/Added 行有意义）——
    public string? NewTranslatedText { get; set; }
    /// <summary>新稿原文（文字微调/拆分片段的新原文；用于确认时写回原句）。</summary>
    public string? NewOriginalText { get; set; }
    /// <summary>新增/拆出句落位场景稳定键（空=与源句同场景）。</summary>
    public string? TargetSceneKey { get; set; }
    /// <summary>新增/拆出句说话人名（确认时解析角色）。</summary>
    public string? TargetCharacterName { get; set; }
    public string? TargetQualifier { get; set; }
    public double? NewInPointMs { get; set; }
    public double? NewMouthCloseMs { get; set; }
    public double? NewAccentMs { get; set; }
    public double? NewMaxDurationMs { get; set; }
    // —— 确认前快照（撤回时原样恢复）——
    public int? SnapshotVersionId { get; set; }
    public string? SnapshotTranslatedText { get; set; }
    public double? SnapshotInPointMs { get; set; }
    public double? SnapshotMouthCloseMs { get; set; }
    public double? SnapshotAccentMs { get; set; }
    public double? SnapshotMaxDurationMs { get; set; }
    public LineState SnapshotLineState { get; set; } = LineState.Active;
    /// <summary>受影响条次 Id（CSV，确认时置待复核）。</summary>
    public string AffectedTakeIds { get; set; } = "";
    /// <summary>受影响条次确认前状态（与 AffectedTakeIds 同序，状态名 CSV），撤回时精确恢复。</summary>
    public string SnapshotTakeStatuses { get; set; } = "";
    /// <summary>快照到的原选用条次 Id（撤回恢复用）。</summary>
    public int? SnapshotPickTakeId { get; set; }
    public PickStatus SnapshotPickStatus { get; set; } = PickStatus.Confirmed;
    public string? SnapshotPickNote { get; set; }
}

/// <summary>语句血缘：拆分/合并在确认后写入，支持追溯与交叉分析。</summary>
public class LineGenealogy
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int PackageId { get; set; }
    public GenealogyKind Kind { get; set; }
    [Indexed] public int SourceLineId { get; set; }
    [Indexed] public int TargetLineId { get; set; }
}

/// <summary>角色当前演员。换演员后历史录音保留（不删除），但不得混入新批次/新修订包。</summary>
public class CharacterCast
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int ProjectId { get; set; }
    [Indexed] public int CharacterId { get; set; }
    public string Actor { get; set; } = "";
    public string ChangedAt { get; set; } = "";
    public string? Note { get; set; }
}

/// <summary>混音交付记录：每次交付写入独立目录，永不原位覆盖已交混音的文件。</summary>
public class MixDelivery
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int ProjectId { get; set; }
    /// <summary>来源修订包；0 表示混录前基线交付。</summary>
    [Indexed] public int RevisionPackageId { get; set; }
    public int RevisionOrdinal { get; set; }
    /// <summary>同包重复交付时递增（v1、v2…）。</summary>
    public int Attempt { get; set; }
    /// <summary>导出目录（工程外，混音侧位置）。</summary>
    public string OutputFolder { get; set; } = "";
    public string DeliveredAt { get; set; } = "";
    public int ExportedFiles { get; set; }
    /// <summary>因换演员被挡下、未混入新批次的历史条次数。</summary>
    public int CastBlocked { get; set; }
    public string? ChangeListPath { get; set; }
}

public class MediaSource
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int ProjectId { get; set; }
    public string Path { get; set; } = "";
    public double FrameRateNum { get; set; }
    public double FrameRateDen { get; set; } = 1;
    public bool VariableFrameRate { get; set; }
    public double DurationMs { get; set; }
    public string? ContentHash { get; set; }
    public string ProbedAt { get; set; } = "";
}
