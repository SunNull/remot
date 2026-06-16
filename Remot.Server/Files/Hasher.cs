using System.Security.Cryptography;

namespace Remot.Server.Files;

public sealed class Hasher
{
    public async Task<string> Sha256Async(Stream s)
    {
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(s);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
