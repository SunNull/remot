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
}
