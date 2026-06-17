using Google.Protobuf;
using Grpc.Core;
using Remot.Protocol;

namespace Remot.Server.Files;

public sealed class FileSender
{
    private const int ChunkSize = 2 * 1024 * 1024;
    private readonly Hasher _hasher;
    public FileSender(Hasher hasher) => _hasher = hasher;

    /// <summary>把文件分块流式写出到 gRPC 下载流。H4:先发 FileHeader(含 size+sha256),客户端据此校验完整性。</summary>
    public async Task SendAsync(string path, IServerStreamWriter<FileChunk> stream, CancellationToken ct)
    {
        var info = new FileInfo(path);
        string sha;
        await using (var hashFs = info.OpenRead())
            sha = await _hasher.Sha256Async(hashFs, ct);
        await stream.WriteAsync(new FileChunk
        {
            Header = new FileHeader { DestPath = path, Size = info.Length, ExpectedSha256 = sha }
        }, ct);

        await using var fs = info.OpenRead();
        var buf = new byte[ChunkSize];
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
            await stream.WriteAsync(new FileChunk { Data = ByteString.CopyFrom(buf, 0, n) }, ct);
    }

    /// <summary>预检:目标文件是否存在、size+sha256 是否一致(用于"跳过未改动")。路径同样需校验(C2)。</summary>
    public async Task<FileCheckResponse> CheckAsync(string destPath, long size, string sha256, CancellationToken ct = default)
    {
        if (!File.Exists(destPath)) return new FileCheckResponse { Exists = false, Matches = false };
        var info = new FileInfo(destPath);
        if (info.Length != size) return new FileCheckResponse { Exists = true, Matches = false };
        await using var fs = info.OpenRead();
        var actual = await _hasher.Sha256Async(fs, ct);
        return new FileCheckResponse { Exists = true, Matches = actual.Equals(sha256, StringComparison.OrdinalIgnoreCase) };
    }
}
