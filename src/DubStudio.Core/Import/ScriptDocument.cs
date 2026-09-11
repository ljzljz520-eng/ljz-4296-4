namespace DubStudio.Core.Import;

public sealed record ScriptDocument(
    string? EpisodeCode,
    string? EpisodeTitle,
    List<ScriptSceneDto> Scenes,
    string? FrameRate,
    bool VariableFrameRate);

public sealed record ScriptSceneDto(string? Code, string? Slug, List<ScriptLineDto> Lines);

public sealed record ScriptLineDto(string Character, string? Qualifier, string Text);
