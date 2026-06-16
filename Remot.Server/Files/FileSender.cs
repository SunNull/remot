using Google.Protobuf;
using Grpc.Core;
using Remot.Protocol;

namespace Remot.Server.Files;

public sealed class FileSender
{
    private const int ChunkSize = 2 * 1024 * 1024;
    private readonly Hasher _hasher;
    public FileSender(Hasher hasher) => _hasher = hasher;

    /// <summary>把文件分块流式写出到 gRPC 下载流(零全量缓冲)。</summary>
    public async Task SendAsync(string path, IServerStreamWriter<FileChunk> stream, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buf = new byte[ChunkSize];
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
            await stream.WriteAsync(new FileChunk { Data = ByteString.CopyFrom(buf, 0, n) });
    }

    /// <summary>预检:目标文件是否存在、size+sha256 是否一致(用于"跳过未改动")。</summary>
    public async Task<FileCheckResponse> CheckAsync(string destPath, long size, string sha256)
    {
        if (!File.Exists(destPath)) return new FileCheckResponse { Exists = false, Matches = false };
        var info = new FileInfo(destPath);
        if (info.Length != size) return new FileCheckResponse { Exists = true, Matches = false };
        await using var fs = info.OpenRead();
        var actual = await _hasher.Sha256Async(fs);
        return new FileCheckResponse { Exists = true, Matches = actual.Equals(sha256, StringComparison.OrdinalIgnoreCase) };
    }
}
