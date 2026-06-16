using Grpc.Core;
using Remot.Client.Channels;
using Remot.Client.Config;
using Remot.Client.Files;
using Remot.Protocol;

namespace Remot.Client;

/// <summary>高层客户端:封装目标配置 + gRPC 调用,统一 RemotResult 返回(不抛异常给调用方)。</summary>
public sealed class RemotClient : IDisposable
{
    private readonly ChannelManager _channels = new();
    private readonly TargetsConfig _config;
    private readonly string _configPath;

    public RemotClient(string configPath)
    {
        _configPath = configPath;
        _config = TargetsConfig.Load(configPath);
    }

    public IReadOnlyList<string> TargetNames => _config.Targets.Keys.ToList();

    public async Task<RemotResult<IReadOnlyList<CommandResult>>> RunCommandAsync(
        string target, IReadOnlyList<string> commands, string shell = "pwsh", int? timeoutMs = null, CancellationToken ct = default)
    {
        var t = _config.Get(target);
        if (t is null) return RemotResult<IReadOnlyList<CommandResult>>.Fail($"未知目标:{target}");
        try
        {
            var stub = new RemotService.RemotServiceClient(_channels.Get(t));
            var req = new CommandRequest { Shell = shell, TimeoutMs = timeoutMs ?? 0 };
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
        var hasher = new Files.Hasher();
        var chunker = new FileChunker();
        var stub = new RemotService.RemotServiceClient(_channels.Get(t));
        var results = new List<TransferResult>();
        try
        {
            foreach (var (src, dst) in files)
            {
                var (sha, size) = await hasher.OfAsync(src);
                // 跳过未改动:size+sha 一致就不传
                var check = await stub.CheckFileAsync(
                    new FileCheckRequest { DestPath = dst, Size = size, Sha256 = sha }, headers: Auth(t), cancellationToken: ct);
                if (check.Matches) { results.Add(new TransferResult { Ok = true, Dest = dst, Bytes = size }); continue; }

                using var upload = stub.Upload(headers: Auth(t), cancellationToken: ct);
                await foreach (var chunk in chunker.StreamAsync(src, dst, sha, size))
                    await upload.RequestStream.WriteAsync(chunk);
                await upload.RequestStream.CompleteAsync();
                results.Add(await upload.ResponseAsync);
            }
            return RemotResult<IReadOnlyList<TransferResult>>.Success(results);
        }
        catch (Exception ex) { return RemotResult<IReadOnlyList<TransferResult>>.Fail(ex.Message); }
    }

    public async Task<RemotResult<None>> DownloadAsync(
        string target, string remotePath, string localPath, CancellationToken ct = default)
    {
        var t = _config.Get(target);
        if (t is null) return RemotResult<None>.Fail($"未知目标:{target}");
        try
        {
            var stub = new RemotService.RemotServiceClient(_channels.Get(t));
            using var call = stub.Download(new FileRequest { Path = remotePath }, headers: Auth(t), cancellationToken: ct);
            await using var fs = File.Create(localPath);
            while (await call.ResponseStream.MoveNext(ct))
                if (call.ResponseStream.Current.KindCase == FileChunk.KindOneofCase.Data)
                    await fs.WriteAsync(call.ResponseStream.Current.Data.Memory);
            return RemotResult<None>.Success(default);
        }
        catch (Exception ex) { return RemotResult<None>.Fail(ex.Message); }
    }

    public void SaveTarget(Target t) { _config.Upsert(t); _config.Save(_configPath); }

    private static Metadata Auth(Target t) => new() { { "authorization", $"Bearer {t.Token}" } };

    public void Dispose() => _channels.Dispose();
}

public readonly record struct None;
