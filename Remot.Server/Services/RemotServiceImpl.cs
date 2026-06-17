using Grpc.Core;
using Remot.Protocol;
using Remot.Server;
using Remot.Server.Execution;
using Remot.Server.Files;
using System.Runtime.CompilerServices;

namespace Remot.Server.Services;

public sealed class RemotServiceImpl : RemotService.RemotServiceBase
{
    private static readonly HashSet<string> AllowedShells = new(StringComparer.OrdinalIgnoreCase) { "pwsh", "powershell", "cmd" };

    private readonly ICommandRunner _runner;
    private readonly FileReceiver _receiver;
    private readonly FileSender _sender;

    public RemotServiceImpl(ICommandRunner runner, FileReceiver receiver, FileSender sender)
    { _runner = runner; _receiver = receiver; _sender = sender; }

    public override async Task RunCommand(CommandRequest request,
        IServerStreamWriter<CommandOutput> stream, ServerCallContext context)
    {
        var shell = string.IsNullOrEmpty(request.Shell) ? "powershell" : request.Shell;
        if (!AllowedShells.Contains(shell))   // H9:白名单
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"不支持的 shell: {shell}"));

        // M10:审计(谁、跑了什么)
        var summary = string.Join(" | ", request.Commands.Select(c => c.Text));
        if (summary.Length > 200) summary = summary[..200] + "...";
        AuditLog.Log($"run peer={context.Peer} shell={shell}: {summary}");

        for (int i = 0; i < request.Commands.Count; i++)
        {
            var spec = new CommandSpec(
                Text: request.Commands[i].Text,
                Shell: shell,
                Cwd: string.IsNullOrEmpty(request.Cwd) ? null : request.Cwd,
                Env: request.Env,
                TimeoutMs: request.TimeoutMs > 0 ? request.TimeoutMs : null,
                MergeStreams: request.MergeStreams);
            // H1:实时把每行输出流式推给客户端
            var r = await _runner.RunAsync(spec, context.CancellationToken, onLine: async line =>
                await stream.WriteAsync(new CommandOutput
                {
                    Chunk = new StreamChunk { IsStderr = line.IsStderr, Data = Google.Protobuf.ByteString.CopyFromUtf8(line.Line + "\n") }
                }));
            await stream.WriteAsync(new CommandOutput
            {
                Result = new CommandResult
                {
                    Index = i,
                    ExitCode = r.ExitCode,
                    Stdout = r.Stdout,
                    Stderr = r.Stderr,
                    DurationMs = r.DurationMs,
                    TimedOut = r.TimedOut,
                    Error = r.Error ?? ""
                }
            });
        }
    }

    public override async Task<TransferResult> Upload(
        IAsyncStreamReader<FileChunk> requestStream, ServerCallContext context)
        => await _receiver.ReceiveAsync(ReadAll(requestStream, context.CancellationToken), context.CancellationToken);

    public override async Task Download(
        FileRequest request, IServerStreamWriter<FileChunk> stream, ServerCallContext context)
    {
        // C2:文件存在性;M6:NotFound 不回传路径
        if (!File.Exists(request.Path))
            throw new RpcException(new Status(StatusCode.NotFound, "file not found"));
        await _sender.SendAsync(request.Path, stream, context.CancellationToken);
    }

    public override async Task<FileCheckResponse> CheckFile(
        FileCheckRequest request, ServerCallContext context)
        => await _sender.CheckAsync(request.DestPath, request.Size, request.Sha256, context.CancellationToken);

    private static async IAsyncEnumerable<FileChunk> ReadAll(IAsyncStreamReader<FileChunk> reader, [EnumeratorCancellation] CancellationToken ct)
    {
        while (await reader.MoveNext(ct))   // M5:透传取消
            yield return reader.Current;
    }
}
