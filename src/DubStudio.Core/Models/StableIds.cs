using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DubStudio.Core.Models;

/// <summary>稳定编号规则：集/场/角色/原语句在重复导入时保持不变。</summary>
public static class StableIds
{
    public static string EpisodeCode(int ordinal) => "E" + ordinal.ToString("00", CultureInfo.InvariantCulture);

    public static string SceneCode(int ordinal) => "S" + ordinal.ToString("000", CultureInfo.InvariantCulture);

    /// <summary>角色稳定码：ASCII/拉丁名直接可用；中日韩等字符用名称哈希避免跨系统编码问题。
    /// 同名无消歧限定的角色共享同一角色码（视为同一人）；带限定则追加限定。</summary>
    public static string CharacterCode(string displayName, string? qualifier)
    {
        var code = "C_" + NamePart(displayName);
        if (!string.IsNullOrWhiteSpace(qualifier))
            code += "#" + NamePart(qualifier);
        return code;
    }

    /// <summary>保留可读的中日韩名称；仅替换编号/路径危险字符；超长才退化为哈希。</summary>
    private static string NamePart(string raw)
    {
        var name = raw.Trim();
        if (name.Length == 0) return ShortHash(name);
        var sanitized = new StringBuilder(name.Length);
        foreach (var ch in name)
            sanitized.Append(ch == ' ' || "|#/\\:*?\"<>".IndexOf(ch) >= 0 ? '_' : ch);
        if (sanitized.Length <= 24) return sanitized.ToString();
        return ShortHash(name);
    }

    /// <summary>行稳定键。occurrence=同一(场,角色,原文)出现的第 n 次，支持同角多句与重复台词。</summary>
    public static string LineKey(string episodeCode, string sceneCode, string characterCode, string originalText, int occurrence)
    {
        return string.Join('|', episodeCode, sceneCode, characterCode, "o" + occurrence.ToString(CultureInfo.InvariantCulture), TextHash(originalText));
    }

    public static string LineCode(string episodeCode, string sceneCode, string characterCode, int characterSlot)
        => $"{episodeCode}-{sceneCode}-{characterCode}-{characterSlot.ToString("00", CultureInfo.InvariantCulture)}";

    public static string TextHash(string text) => ShortHash(Normalize(text));

    public static string ShortHash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 8);
    }

    /// <summary>归一化：去首尾空白、合并连续空白，保证“同一句话”不因排版差异产生不同键。</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool prevSpace = false;
        foreach (var ch in s.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else { sb.Append(ch); prevSpace = false; }
        }
        return sb.ToString();
    }

}
