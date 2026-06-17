using Google.Protobuf;
using Remot.Protocol;

namespace Remot.Server.Files;

public sealed class FileReceiver
{
    private const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024; // 2GB 默认上限
    private readonly Hasher _hasher;
    private readonly IReadOnlyList<string> _allowedBasePaths;
    private readonly long _maxBytes;

    public FileReceiver(Hasher hasher, IReadOnlyList<string>? allowedBasePaths = null, long? maxBytes = null)
    {
        _hasher = hasher;
        _allowedBasePaths = allowedBasePaths ?? Array.Empty<string>();
        _maxBytes = maxBytes ?? DefaultMaxBytes;
    }

    public async Task<TransferResult> ReceiveAsync(IAsyncEnumerable<FileChunk> stream, CancellationToken ct = default)
    {
        FileHeader? header = null;
        string dest = "";
        string tempPath = "";
        long received = 0;
        FileStream? fs = null;
        try
        {
            await foreach (var chunk in stream.WithCancellation(ct))   // M5:透传取消
            {
                if (chunk.KindCase == FileChunk.KindOneofCase.Header)
                {
                    header = chunk.Header;
                    dest = PathValidator.Validate(header.DestPath, _allowedBasePaths);   // C1:路径校验
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    tempPath = dest + "." + Guid.NewGuid().ToString("N") + ".remot-tmp"; // M4:唯一临时名
                    fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None); // H5+L8:创建即存在(空文件 OK)+单次打开
                    continue;
                }
                if (header is null || fs is null) return Err("missing header", "");
                // M3:以声明大小 + 硬上限双重限制
                if (header.Size > 0 && received + chunk.Data.Length > header.Size)
                    return Err($"数据超过声明大小 {header.Size}", dest);
                received += chunk.Data.Length;
                if (received > _maxBytes) return Err($"超过最大允许大小 {_maxBytes}", dest);
                await fs.WriteAsync(chunk.Data.Memory, ct);
            }

            fs?.Dispose(); fs = null;
            if (header is null) return Err("missing header", "");

            if (header.Size > 0 && received != header.Size)
                return Err($"大小不匹配(声明 {header.Size}, 实际 {received})", dest);

            await using (var readForHash = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var actual = await _hasher.Sha256Async(readForHash, ct);
                if (!string.IsNullOrEmpty(header.ExpectedSha256) &&
                    !actual.Equals(header.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tempPath);
                    return Err($"sha256 校验失败(期望 {header.ExpectedSha256}, 实际 {actual})", dest);
                }
            }

            File.Move(tempPath, dest, overwrite: true);
            return new TransferResult { Ok = true, Dest = dest, Bytes = received };
        }
        catch (Exception ex)
        {
            fs?.Dispose();
            TryDelete(tempPath);
            return Err(ex.Message, header?.DestPath ?? "");
        }
    }

    private static TransferResult Err(string msg, string dest) => new() { Ok = false, Dest = dest, Error = msg };
    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
