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
        TransferResult? error = null;
        try
        {
            await foreach (var chunk in stream.WithCancellation(ct))   // M5:透传取消
            {
                if (chunk.KindCase == FileChunk.KindOneofCase.Header)
                {
                    header = chunk.Header;
                    dest = PathValidator.Validate(header.DestPath, _allowedBasePaths);   // C1:路径校验
                    if (header.Size < 0) { error = Err("声明大小不能为负", dest); break; }   // H3
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    tempPath = dest + "." + Guid.NewGuid().ToString("N") + ".remot-tmp"; // M4:唯一临时名
                    fs?.Dispose();
                    fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None); // H5+L8:创建即存在(空文件 OK)+单次打开
                    continue;
                }
                if (header is null || fs is null) { error = Err("missing header", ""); break; }
                // H3:以声明大小为硬上限(Size<=0 时任何数据块都越界 → 拒绝)
                if (received + chunk.Data.Length > header.Size)
                { error = Err($"数据超过声明大小 {header.Size}", dest); break; }
                received += chunk.Data.Length;
                if (received > _maxBytes) { error = Err($"超过最大允许大小 {_maxBytes}", dest); break; }
                await fs.WriteAsync(chunk.Data.Memory, ct);
            }

            fs?.Dispose(); fs = null;   // 释放写入句柄,方可读哈希 / Move

            if (error is null && header is null) error = Err("missing header", "");

            if (error is null)
            {
                var h = header!;   // error 为空 ⇒ header 非 null(上一行已保证)
                // H3:实际接收大小必须等于声明(空文件 Size=0 + 0 字节合法)
                if (received != h.Size)
                    error = Err($"大小不匹配(声明 {h.Size}, 实际 {received})", dest);
                // H3:非空文件(Size>0)必须提供 sha256 做完整性校验,否则拒绝
                else if (h.Size > 0 && string.IsNullOrEmpty(h.ExpectedSha256))
                    error = Err("缺少 sha256(非空文件必须提供完整性校验值)", dest);
                else if (!string.IsNullOrEmpty(h.ExpectedSha256))
                {
                    await using var readForHash = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var actual = await _hasher.Sha256Async(readForHash, ct);
                    if (!actual.Equals(h.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                        error = Err($"sha256 校验失败(期望 {h.ExpectedSha256}, 实际 {actual})", dest);
                }

                if (error is null)
                {
                    // BUG-7:客户端始终 overwrite=true,overwrite=false 分支不可达,简化
                    File.Move(tempPath, dest, overwrite: true);
                    return new TransferResult { Ok = true, Dest = dest, Bytes = received };
                }
            }
        }
        catch (Exception ex)
        {
            error = Err(ex.Message, header?.DestPath ?? "");
        }
        finally
        {
            // M1:任何路径都释放句柄;失败时清理临时文件(不再泄漏/残留)
            fs?.Dispose();
            if (error is not null) TryDelete(tempPath);
        }
        return error!;
    }

    private static TransferResult Err(string msg, string dest) => new() { Ok = false, Dest = dest, Error = msg };
    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
