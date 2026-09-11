using SQLite;

namespace DubStudio.Core.Models;

public enum LineState { Active = 0, Retired = 1 }
public enum TakeStatus { Usable = 0, NeedsReview = 1, Rejected = 2 }
public enum PickStatus { Confirmed = 0, PendingReview = 1 }
public enum ReviewKind { TextChanged = 0, ShiftedLine = 1, ReimportConflict = 2, ExternalFileChanged = 3 }

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
    public ReviewKind Kind { get; set; }
    public string Message { get; set; } = "";
    public bool Resolved { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? ResolvedAt { get; set; }
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
