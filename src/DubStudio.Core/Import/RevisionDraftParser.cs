using System.Text.Json;

namespace DubStudio.Core.Import;

/// <summary>修订稿解析：
/// 1) JSON：{ "episodeCode":"E01", "timelineShiftMs":0, "reason":"…", "submittedBy":"…",
///    "scenes":[{ "code":"S001","lines":[
///    {"character":"田中","text":"原文","translatedText":"新译文",
///     "inPointMs":1000,"mouthCloseMs":2200,"accentMs":null,"maxDurationMs":1400} ]}] }
/// 2) 纯文本：复用剧本文本格式（EPISODE/SCENE/角色: 台词），只携带原文，
///    适合纯删/增/拆分前的结构调整；时序与新译文留空表示不变。</summary>
public static class RevisionDraftParser
{
    public static RevisionDraft Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("修订稿为空");
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            return ParseJson(content);
        return ParseText(content);
    }

    private static RevisionDraft ParseJson(string content)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var dto = JsonSerializer.Deserialize<JsonDraft>(content, opts)
                  ?? throw new InvalidDataException("修订稿 JSON 无法解析");
        var scenes = (dto.Scenes ?? new List<JsonDraftScene>())
            .Select(s => new RevisionSceneDraft(s.Code?.Trim(), s.Slug?.Trim(),
                (s.Lines ?? new List<JsonDraftLine>())
                    .Where(l => !string.IsNullOrWhiteSpace(l.Character) && !string.IsNullOrWhiteSpace(l.Text))
                    .Select(l => new RevisionLineDraft(
                        l.Character!.Trim(),
                        l.Qualifier?.Trim(),
                        l.Text!.Trim(),
                        string.IsNullOrWhiteSpace(l.TranslatedText) ? null : l.TranslatedText!.Trim(),
                        l.InPointMs, l.MouthCloseMs, l.AccentMs, l.MaxDurationMs))
                    .ToList()))
            .ToList();
        return new RevisionDraft(dto.EpisodeCode?.Trim(), scenes, dto.TimelineShiftMs ?? 0,
            string.IsNullOrWhiteSpace(dto.Reason) ? null : dto.Reason!.Trim(),
            string.IsNullOrWhiteSpace(dto.SubmittedBy) ? null : dto.SubmittedBy!.Trim());
    }

    private static RevisionDraft ParseText(string content)
    {
        var doc = ScriptParser.Parse(content);
        var scenes = doc.Scenes
            .Select(s => new RevisionSceneDraft(s.Code, s.Slug,
                s.Lines.Select(l => new RevisionLineDraft(l.Character, l.Qualifier, l.Text)).ToList()))
            .ToList();
        return new RevisionDraft(doc.EpisodeCode, scenes, 0, null, null);
    }

    private sealed class JsonDraft
    {
        public string? EpisodeCode { get; set; }
        public List<JsonDraftScene>? Scenes { get; set; }
        public double? TimelineShiftMs { get; set; }
        public string? Reason { get; set; }
        public string? SubmittedBy { get; set; }
    }
    private sealed class JsonDraftScene
    {
        public string? Code { get; set; }
        public string? Slug { get; set; }
        public List<JsonDraftLine>? Lines { get; set; }
    }
    private sealed class JsonDraftLine
    {
        public string? Character { get; set; }
        public string? Qualifier { get; set; }
        public string? Text { get; set; }
        public string? TranslatedText { get; set; }
        public double? InPointMs { get; set; }
        public double? MouthCloseMs { get; set; }
        public double? AccentMs { get; set; }
        public double? MaxDurationMs { get; set; }
    }
}
