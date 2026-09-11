namespace DubStudio.Core.Import;

/// <summary>译配编辑提交的混录后修订稿。行内容为“新稿值”，
/// 由修订服务按稳定语句身份与在制剧本逐条比对。时序字段为 null 表示该句时序不变。</summary>
public sealed record RevisionDraft(
    string? EpisodeCode,
    List<RevisionSceneDraft> Scenes,
    double TimelineShiftMs = 0,
    string? Reason = null,
    string? SubmittedBy = null);

public sealed record RevisionSceneDraft(string? Code, string? Slug, List<RevisionLineDraft> Lines);

public sealed record RevisionLineDraft(
    string Character,
    string? Qualifier,
    /// <summary>原文（用于稳定身份比对）；仅文字微调时也可填改后的原文。</summary>
    string Text,
    /// <summary>新译文；null 表示译文未改。</summary>
    string? TranslatedText = null,
    double? InPointMs = null,
    double? MouthCloseMs = null,
    double? AccentMs = null,
    double? MaxDurationMs = null);
