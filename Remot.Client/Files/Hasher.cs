using System.Security.Cryptography;

namespace Remot.Client.Files;

public sealed class Hasher
{
    public async Task<(string Sha256, long Size)> OfAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var h = await sha.ComputeHashAsync(fs);
        return (Convert.ToHexString(h).ToLowerInvariant(), new FileInfo(path).Length);
    }
}
