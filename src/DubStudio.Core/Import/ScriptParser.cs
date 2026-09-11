using System.Text;
using System.Text.Json;

namespace DubStudio.Core.Import;

/// <summary>剧本解析器。支持两种输入：
/// 1) 纯文本：
///    EPISODE E01 标题
///    SCENE S012 街道·夜
///    田中@少年: 你好啊。
///    （延续行视为同一句）
///    # 注释行
///    FPS 29.97
///    VFR
/// 2) JSON：{ "episodeCode":"E01", "scenes":[{ "code":"S001","lines":[
///    {"character":"田中","qualifier":"少年","text":"..."} ]}] }</summary>
public static class ScriptParser
{
    public static ScriptDocument Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("剧本为空");
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            return ParseJson(content);
        return ParseText(content);
    }

    private static ScriptDocument ParseJson(string content)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var dto = JsonSerializer.Deserialize<JsonDoc>(content, opts)
                  ?? throw new InvalidDataException("剧本 JSON 无法解析");
        var scenes = (dto.Scenes ?? new List<JsonScene>())
            .Select(s => new ScriptSceneDto(s.Code, s.Slug,
                (s.Lines ?? new List<JsonLine>())
                    .Where(l => !string.IsNullOrWhiteSpace(l.Character) && !string.IsNullOrWhiteSpace(l.Text))
                    .Select(l => new ScriptLineDto(l.Character!.Trim(), l.Qualifier?.Trim(), l.Text!.Trim()))
                    .ToList()))
            .ToList();
        return new ScriptDocument(dto.EpisodeCode?.Trim(), dto.EpisodeTitle?.Trim(), scenes,
            dto.FrameRate?.Trim(), dto.VariableFrameRate);
    }

    private static ScriptDocument ParseText(string content)
    {
        string? episodeCode = null, episodeTitle = null, fps = null;
        bool vfr = false;
        var scenes = new List<ScriptSceneDto>();
        ScriptSceneDto? current = null;
        ScriptLineDto? currentLine = null;

        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) { currentLine = null; continue; }
            if (line.StartsWith("#") || line.StartsWith("//")) continue;

            if (line.StartsWith("EPISODE", StringComparison.OrdinalIgnoreCase))
            {
                var rest = line["EPISODE".Length..].Trim();
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                episodeCode = parts.FirstOrDefault();
                episodeTitle = parts.Length > 1 ? parts[1] : null;
                continue;
            }
            if (line.StartsWith("SCENE", StringComparison.OrdinalIgnoreCase))
            {
                var rest = line["SCENE".Length..].Trim();
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                current = new ScriptSceneDto(parts.FirstOrDefault(), parts.Length > 1 ? parts[1] : null, new());
                scenes.Add(current);
                currentLine = null;
                continue;
            }
            if (line.StartsWith("FPS", StringComparison.OrdinalIgnoreCase))
            {
                fps = line["FPS".Length..].Trim();
                continue;
            }
            if (line.Equals("VFR", StringComparison.OrdinalIgnoreCase))
            {
                vfr = true;
                continue;
            }

            var colon = IndexOfSpeechColon(line);
            if (colon >= 0 && current != null)
            {
                var speaker = line[..colon].Trim();
                var text = line[(colon + 1)..].Trim();
                string? qualifier = null;
                var at = speaker.IndexOf('@');
                if (at >= 0)
                {
                    qualifier = speaker[(at + 1)..].Trim();
                    speaker = speaker[..at].Trim();
                }
                currentLine = new ScriptLineDto(speaker, qualifier, text);
                current.Lines.Add(currentLine);
            }
            else if (currentLine != null && current != null)
            {
                // 无说话人前缀：并入上一句
                currentLine = currentLine with { Text = currentLine.Text + " " + line };
                var idx = current.Lines.Count - 1;
                current.Lines[idx] = currentLine;
            }
        }

        return new ScriptDocument(episodeCode, episodeTitle, scenes, fps, vfr);
    }

    private static int IndexOfSpeechColon(string line)
    {
        var half = line.IndexOf(':');
        var full = line.IndexOf('：');
        if (half < 0) return full;
        if (full < 0) return half;
        return Math.Min(half, full);
    }

    private sealed class JsonDoc
    {
        public string? EpisodeCode { get; set; }
        public string? EpisodeTitle { get; set; }
        public List<JsonScene>? Scenes { get; set; }
        public string? FrameRate { get; set; }
        public bool VariableFrameRate { get; set; }
    }
    private sealed class JsonScene
    {
        public string? Code { get; set; }
        public string? Slug { get; set; }
        public List<JsonLine>? Lines { get; set; }
    }
    private sealed class JsonLine
    {
        public string? Character { get; set; }
        public string? Qualifier { get; set; }
        public string? Text { get; set; }
    }
}
