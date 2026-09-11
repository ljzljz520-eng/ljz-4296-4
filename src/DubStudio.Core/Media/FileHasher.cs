using System.Security.Cryptography;

namespace DubStudio.Core.Media;

public static class FileHasher
{
    public static async Task<string?> Sha256Async(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public static string? Sha256(string path) => Sha256Async(path).GetAwaiter().GetResult();
}
