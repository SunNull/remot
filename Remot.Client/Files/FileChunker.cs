using Google.Protobuf;
using Remot.Protocol;

namespace Remot.Client.Files;

public sealed class FileChunker(int chunkSize = 2 * 1024 * 1024)
{
    public async IAsyncEnumerable<FileChunk> StreamAsync(string src, string dest, string sha256, long size)
    {
        yield return new FileChunk
        {
            Header = new FileHeader { DestPath = dest, ExpectedSha256 = sha256, Size = size, Overwrite = true }
        };
        await using var fs = File.OpenRead(src);
        var buf = new byte[chunkSize];
        int n;
        while ((n = await fs.ReadAsync(buf)) > 0)
            yield return new FileChunk { Data = ByteString.CopyFrom(buf, 0, n) };
    }
}
