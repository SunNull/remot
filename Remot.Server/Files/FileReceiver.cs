using Google.Protobuf;
using Remot.Protocol;

namespace Remot.Server.Files;

public sealed class FileReceiver
{
    private readonly Hasher _hasher;
    public FileReceiver(Hasher hasher) => _hasher = hasher;

    public async Task<TransferResult> ReceiveAsync(IAsyncEnumerable<FileChunk> stream)
    {
        FileHeader? header = null;
        string tempPath = "";
        try
        {
            await foreach (var chunk in stream)
            {
                if (chunk.KindCase == FileChunk.KindOneofCase.Header)
                {
                    header = chunk.Header;
                    Directory.CreateDirectory(Path.GetDirectoryName(header.DestPath)!);
                    tempPath = header.DestPath + ".remot-tmp";
                    continue;
                }
                if (header is null) return Err("missing header", "");
                // 每个数据块追加写临时文件(流式,零全量缓冲)。
                await using (var fs = new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.None))
                    await fs.WriteAsync(chunk.Data.Memory);
            }

            if (header is null) return Err("missing header", "");
            if (!File.Exists(tempPath)) return Err("empty upload", header.DestPath);

            await using (var readForHash = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var actual = await _hasher.Sha256Async(readForHash);
                if (!actual.Equals(header.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tempPath);
                    return Err($"sha256 mismatch (expected {header.ExpectedSha256}, got {actual})", header.DestPath);
                }
            }

            if (File.Exists(header.DestPath) && !header.Overwrite)
                return Err("dest exists and overwrite=false", header.DestPath);

            File.Move(tempPath, header.DestPath, overwrite: header.Overwrite);
            return new TransferResult { Ok = true, Dest = header.DestPath, Bytes = new FileInfo(header.DestPath).Length };
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            return Err(ex.Message, header?.DestPath ?? "");
        }
    }

    private static TransferResult Err(string msg, string dest) => new() { Ok = false, Dest = dest, Error = msg };
    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
