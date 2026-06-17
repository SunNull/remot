using System.Runtime.CompilerServices;
using Google.Protobuf;
using Remot.Protocol;

namespace Remot.Client.Files;

/// <summary>上传分块器。H4:流式哈希 + 流式分块,避免整文件读入内存(大文件 OOM)。</summary>
public sealed class FileChunker(int chunkSize = 2 * 1024 * 1024)
{
    /// <summary>流式计算 SHA256 与文件大小(零全量缓冲)。</summary>
    public async Task<(string Sha256, long Size)> HashAsync(string src, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(src);
        var hash = await new Hasher().Sha256Async(fs, ct);   // SHA256 收拢到 Hasher
        return (hash, fs.Length);
    }

    /// <summary>产出上传分块:先 Header(含 sha/size),再数据块(流式读,零全量缓冲)。</summary>
    public async IAsyncEnumerable<FileChunk> StreamAsync(string src, string dest, string sha256, long size,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new FileChunk
        {
            Header = new FileHeader { DestPath = dest, ExpectedSha256 = sha256, Size = size, Overwrite = true }
        };
        await using var fs = File.OpenRead(src);
        var buf = new byte[chunkSize];
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
            yield return new FileChunk { Data = ByteString.CopyFrom(buf, 0, n) };
    }
}
