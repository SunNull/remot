using System.Security.Cryptography;

namespace Remot.Server.Files;

public sealed class Hasher
{
    public async Task<string> Sha256Async(Stream s, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(s, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
