using Grpc.Core;
using Remot.Protocol;
using Remot.Server.Execution;
using Remot.Server.Files;

namespace Remot.Server.Services;

public sealed class RemotServiceImpl : RemotService.RemotServiceBase
{
    private readonly ICommandRunner _runner;
    private readonly FileReceiver _receiver;
    private readonly FileSender _sender;

    public RemotServiceImpl(ICommandRunner runner, FileReceiver receiver, FileSender sender)
    { _runner = runner; _receiver = receiver; _sender = sender; }

    public override async Task RunCommand(CommandRequest request,
        IServerStreamWriter<CommandOutput> stream, ServerCallContext context)
    {
        for (int i = 0; i < request.Commands.Count; i++)
        {
            var spec = new CommandSpec(
                Text: request.Commands[i].Text,
                Shell: string.IsNullOrEmpty(request.Shell) ? "powershell" : request.Shell,
                Cwd: string.IsNullOrEmpty(request.Cwd) ? null : request.Cwd,
                Env: request.Env,
                TimeoutMs: request.TimeoutMs > 0 ? request.TimeoutMs : null,
                MergeStreams: request.MergeStreams);
            var r = await _runner.RunAsync(spec, context.CancellationToken);
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
        => await _receiver.ReceiveAsync(ReadAll(requestStream));

    public override async Task Download(
        FileRequest request, IServerStreamWriter<FileChunk> stream, ServerCallContext context)
    {
        if (!File.Exists(request.Path)) throw new FileNotFoundException(request.Path);
        await _sender.SendAsync(request.Path, stream, context.CancellationToken);
    }

    public override async Task<FileCheckResponse> CheckFile(
        FileCheckRequest request, ServerCallContext context)
        => await _sender.CheckAsync(request.DestPath, request.Size, request.Sha256);

    private static async IAsyncEnumerable<FileChunk> ReadAll(IAsyncStreamReader<FileChunk> reader)
    {
        while (await reader.MoveNext(CancellationToken.None))
            yield return reader.Current;
    }
}
