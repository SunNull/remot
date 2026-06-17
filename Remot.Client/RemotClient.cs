using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Remot.Client.Channels;
using Remot.Client.Config;
using Remot.Protocol;

namespace Remot.Client;

/// <summary>高层客户端:封装目标配置 + gRPC 调用,统一 RemotResult 返回(不抛异常给调用方)。</summary>
public sealed class RemotClient : IDisposable
{
    private const int ChunkSize = 2 * 1024 * 1024;

    private readonly ChannelManager _channels = new();
    private readonly TargetsConfig _config;
    private readonly string _configPath;

    public RemotClient(string configPath)
    {
        _configPath = configPath;
        _config = TargetsConfig.Load(configPath);
    }

    public IReadOnlyList<string> TargetNames => _config.Targets.Keys.ToList();

    /// <summary>列出所有目标的 名称/地址/端口。</summary>
    public IReadOnlyList<(string Name, string Host, int Port)> ListTargets() =>
        _config.Targets.Select(kv => (kv.Key, kv.Value.Host, kv.Value.Port)).ToList();

    /// <summary>供 CLI 的 ping/pair-自检直接用。</summary>
    public Grpc.Net.Client.GrpcChannel GetChannel(Target t) => _channels.Get(t);

    public async Task<RemotResult<IReadOnlyList<CommandResult>>> RunCommandAsync(
        string target, IReadOnlyList<string> commands, string shell = "powershell", int? timeoutMs = null,
        string? cwd = null, CancellationToken ct = default)
    {
        var t = _config.Get(target);
        if (t is null) return RemotResult<IReadOnlyList<CommandResult>>.Fail($"未知目标:{target}");
        try
        {
            var stub = new RemotService.RemotServiceClient(_channels.Get(t));
            var req = new CommandRequest { Shell = shell, TimeoutMs = timeoutMs ?? 0, Cwd = cwd ?? "" };
            req.Commands.AddRange(commands.Select(c => new Command { Text = c }));
            using var call = stub.RunCommand(req, headers: Auth(t), cancellationToken: ct);
            var results = new List<CommandResult>();
            while (await call.ResponseStream.MoveNext(ct))
                if (call.ResponseStream.Current.KindCase == CommandOutput.KindOneofCase.Result)
                    results.Add(call.ResponseStream.Current.Result);
            return RemotResult<IReadOnlyList<CommandResult>>.Success(results);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        { return RemotResult<IReadOnlyList<CommandResult>>.Fail("token 无效"); }
        catch (Exception ex) { return RemotResult<IReadOnlyList<CommandResult>>.Fail(ex.Message); }
    }

    public async Task<RemotResult<IReadOnlyList<TransferResult>>> UploadAsync(
        string target, IReadOnlyList<(string Src, string Dst)> files, CancellationToken ct = default)
    {
        var t = _config.Get(target);
        if (t is null) return RemotResult<IReadOnlyList<TransferResult>>.Fail($"未知目标:{target}");
        var stub = new RemotService.RemotServiceClient(_channels.Get(t));
        var meta = Auth(t);
        var results = new ConcurrentBag<(int Index, TransferResult Res)>();

        // M12:并发上传(上限 8);M8:逐文件独立 try/catch,任一失败不影响其余。
        await Parallel.ForEachAsync(
            files.Select((f, i) => (File: f, Index: i)),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (item, ict) =>
            {
                try
                {
                    var (src, dst) = item.File;
                    // M13:单次读入内存,作为哈希与分块的同一来源(避免双开文件的 TOCTOU)。
                    var bytes = await File.ReadAllBytesAsync(src, ict);
                    string sha;
                    using (var sha256 = SHA256.Create())
                        sha = Convert.ToHexString(await sha256.ComputeHashAsync(new MemoryStream(bytes), ict)).ToLowerInvariant();

                    var check = await stub.CheckFileAsync(
                        new FileCheckRequest { DestPath = dst, Size = bytes.Length, Sha256 = sha }, headers: meta, cancellationToken: ict);
                    if (check.Matches)
                    { results.Add((item.Index, new TransferResult { Ok = true, Dest = dst, Bytes = bytes.Length })); return; }

                    using var upload = stub.Upload(headers: meta, cancellationToken: ict);
                    await upload.RequestStream.WriteAsync(new FileChunk
                    {
                        Header = new FileHeader { DestPath = dst, ExpectedSha256 = sha, Size = bytes.Length, Overwrite = true }
                    }, ict);
                    for (int off = 0; off < bytes.Length; off += ChunkSize)
                    {
                        int len = Math.Min(ChunkSize, bytes.Length - off);
                        await upload.RequestStream.WriteAsync(new FileChunk { Data = ByteString.CopyFrom(bytes, off, len) }, ict);
                    }
                    await upload.RequestStream.CompleteAsync();
                    results.Add((item.Index, await upload.ResponseAsync));
                }
                catch (Exception ex)
                {
                    results.Add((item.Index, new TransferResult { Ok = false, Dest = item.File.Dst, Error = ex.Message }));
                }
            });

        var ordered = results.OrderBy(r => r.Index).Select(r => r.Res).ToList();
        return RemotResult<IReadOnlyList<TransferResult>>.Success(ordered);
    }

    public async Task<RemotResult<None>> DownloadAsync(
        string target, string remotePath, string localPath, CancellationToken ct = default)
    {
        var t = _config.Get(target);
        if (t is null) return RemotResult<None>.Fail($"未知目标:{target}");
        var tmp = localPath + ".remot-tmp";
        try
        {
            var stub = new RemotService.RemotServiceClient(_channels.Get(t));
            using var call = stub.Download(new FileRequest { Path = remotePath }, headers: Auth(t), cancellationToken: ct);

            string? expectedSha = null;
            await using (var fs = File.Create(tmp))   // M7:写到临时文件,成功才落地
            {
                while (await call.ResponseStream.MoveNext(ct))
                {
                    var c = call.ResponseStream.Current;
                    if (c.KindCase == FileChunk.KindOneofCase.Header)
                        expectedSha = c.Header.ExpectedSha256;
                    else if (c.KindCase == FileChunk.KindOneofCase.Data)
                        await fs.WriteAsync(c.Data.Memory, ct);
                }
            }

            // H4:完整性校验
            if (expectedSha is not null)
            {
                string actual;
                await using (var r = File.OpenRead(tmp))
                {
                    using var sha256 = SHA256.Create();
                    actual = Convert.ToHexString(await sha256.ComputeHashAsync(r, ct)).ToLowerInvariant();
                }
                if (!actual.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                { TryDelete(tmp); return RemotResult<None>.Fail($"下载完整性校验失败(期望 {expectedSha}, 实际 {actual})"); }
            }

            File.Move(tmp, localPath, overwrite: true);
            return RemotResult<None>.Success(default);
        }
        catch (Exception ex) { TryDelete(tmp); return RemotResult<None>.Fail(ex.Message); }
    }

    public void SaveTarget(Target t)
    {
        _config.Upsert(t);
        _config.Save(_configPath);
        _channels.Invalidate(t.Name);   // H8:配置变更后失效旧通道
    }

    private static Metadata Auth(Target t) => new() { { "authorization", $"Bearer {t.Token}" } };
    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    public void Dispose() => _channels.Dispose();
}

public readonly record struct None;
