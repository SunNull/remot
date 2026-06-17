using System.Security.Cryptography;

namespace Remot.Protocol;

/// <summary>共享 SHA256 工具(L6 去重:Server 与 Client 共用)。</summary>
public sealed class Hasher
{
    public async Task<string> Sha256Async(Stream s, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(s, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<(string Sha256, long Size)> OfAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs);
        return (Convert.ToHexString(hash).ToLowerInvariant(), new FileInfo(path).Length);
    }

    public string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
