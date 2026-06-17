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
    private readonly Remot.Server.Config.ServerConfig _config;
    private readonly SessionManager _sessions;

    public RemotServiceImpl(ICommandRunner runner, FileReceiver receiver, FileSender sender, Remot.Server.Config.ServerConfig config, SessionManager sessions)
    { _runner = runner; _receiver = receiver; _sender = sender; _config = config; _sessions = sessions; }

    public override async Task RunCommand(CommandRequest request,
        IServerStreamWriter<CommandOutput> stream, ServerCallContext context)
    {
        if (request.Commands.Count == 0)   // L11:空命令显式报错,而非静默无返回
            throw new RpcException(new Status(StatusCode.InvalidArgument, "commands 为空"));
        var shell = ShellDetector.Resolve(request.Shell);   // 优化1:空/auto → pwsh 优先
        if (!AllowedShells.Contains(shell))   // H9:白名单
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"不支持的 shell: {shell}"));

        // M10:审计(谁、跑了什么)
        var summary = string.Join(" | ", request.Commands.Select(c => c.Text));
        if (summary.Length > 200) summary = summary[..200] + "...";
        AuditLog.Log($"run peer={context.Peer} shell={shell}: {summary}");

        var ct = context.CancellationToken;
        string? cwd = string.IsNullOrEmpty(request.Cwd) ? null : request.Cwd;
        int? timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : null;

        // 先对所有命令做危险拦截,分 blocked / executable
        var plan = new (string Text, string? Blocked)[request.Commands.Count];
        for (int i = 0; i < request.Commands.Count; i++)
        {
            var cmdText = request.Commands[i].Text;
            var br = Remot.Server.Security.CommandGuard.Check(cmdText,
                _config.BlockedCommands.Count > 0 ? _config.BlockedCommands : null,
                _config.ProtectedServices.Count > 0 ? _config.ProtectedServices : null,
                _config.ProtectedPaths.Count > 0 ? _config.ProtectedPaths : null);
            if (br is not null) AuditLog.Log($"⛔ BLOCKED: {br} | cmd: {Truncate(cmdText, 100)}");
            plan[i] = (cmdText, br);
        }

        CommandSpec SpecFor(string text) => new(Text: text, Shell: shell, Cwd: cwd, Env: request.Env,
            TimeoutMs: timeoutMs, MergeStreams: request.MergeStreams);

        if (shell is "pwsh" or "powershell")
        {
            // 优化2:PowerShell 批量——一个进程跑完所有可执行命令,sentinel 切分每条结果
            var execSpecs = plan.Where(p => p.Blocked is null).Select(p => SpecFor(p.Text)).ToList();
            var execResults = execSpecs.Count > 0
                ? await _runner.RunBatchAsync(execSpecs, ct, onLine: async (_, line) =>
                    await stream.WriteAsync(new CommandOutput
                    {
                        Chunk = new StreamChunk { IsStderr = line.IsStderr, Data = Google.Protobuf.ByteString.CopyFromUtf8(line.Line + "\n") }
                    }))
                : new List<CommandRunResult>();
            int ei = 0;
            for (int i = 0; i < plan.Length; i++)
            {
                if (plan[i].Blocked is string br)
                    await WriteResult(stream, i, new CommandRunResult(-1, "", "", 0, false, br));
                else
                    await WriteResult(stream, i, execResults[ei++]);
            }
        }
        else   // cmd 逐条(cmd 的 %errorlevel 在批量拼接下不可靠,保持逐条)
        {
            for (int i = 0; i < plan.Length; i++)
            {
                if (plan[i].Blocked is string br)
                {
                    await WriteResult(stream, i, new CommandRunResult(-1, "", "", 0, false, br));
                    continue;
                }
                var r = await _runner.RunAsync(SpecFor(plan[i].Text), ct, onLine: async line =>
                    await stream.WriteAsync(new CommandOutput
                    {
                        Chunk = new StreamChunk { IsStderr = line.IsStderr, Data = Google.Protobuf.ByteString.CopyFromUtf8(line.Line + "\n") }
                    }));
                await WriteResult(stream, i, r);
            }
        }
    }

    private static async Task WriteResult(IServerStreamWriter<CommandOutput> stream, int index, CommandRunResult r) =>
        await stream.WriteAsync(new CommandOutput
        {
            Result = new CommandResult
            {
                Index = index, ExitCode = r.ExitCode, Stdout = r.Stdout, Stderr = r.Stderr,
                DurationMs = r.DurationMs, TimedOut = r.TimedOut, Error = r.Error ?? ""
            }
        });

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public override async Task<TransferResult> Upload(
        IAsyncStreamReader<FileChunk> requestStream, ServerCallContext context)
        => await _receiver.ReceiveAsync(ReadAll(requestStream, context.CancellationToken), context.CancellationToken);

    public override async Task Download(
        FileRequest request, IServerStreamWriter<FileChunk> stream, ServerCallContext context)
    {
        // L5:路径越界 → PermissionDenied(而非 Internal);C2:不存在 → NotFound(不回传路径)
        string safe;
        try { safe = PathValidator.Validate(request.Path, _config.AllowedBasePaths); }
        catch (UnauthorizedAccessException) { throw new RpcException(new Status(StatusCode.PermissionDenied, "access denied")); }
        catch (ArgumentException) { throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid path")); }
        if (!File.Exists(safe))
            throw new RpcException(new Status(StatusCode.NotFound, "file not found"));
        await _sender.SendAsync(safe, stream, context.CancellationToken);
    }

    public override async Task<FileCheckResponse> CheckFile(
        FileCheckRequest request, ServerCallContext context)
        => await _sender.CheckAsync(request.DestPath, request.Size, request.Sha256, context.CancellationToken);

    // ── 优化3:持久会话 ──
    public override Task<OpenSessionResponse> OpenSession(OpenSessionRequest request, ServerCallContext context)
    {
        var shell = ShellDetector.Resolve(string.IsNullOrEmpty(request.Shell) ? "auto" : request.Shell);
        if (!AllowedShells.Contains(shell))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"不支持的 shell: {shell}"));
        try
        {
            var id = _sessions.Open(shell, string.IsNullOrEmpty(request.Cwd) ? null : request.Cwd);
            AuditLog.Log($"session open peer={context.Peer} shell={shell}");
            return Task.FromResult(new OpenSessionResponse { SessionId = id });
        }
        catch (Exception ex)   // cwd 非法等导致 shell 进程启动失败
        {
            AuditLog.Log($"session open 失败: {ex.Message}");
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"打开会话失败(检查 cwd): {ex.Message}"));
        }
    }

    public override async Task RunInSession(RunInSessionRequest request, IServerStreamWriter<CommandOutput> stream, ServerCallContext context)
    {
        if (!_sessions.Exists(request.SessionId))
            throw new RpcException(new Status(StatusCode.NotFound, "session 不存在或已关闭"));
        // 危险命令拦截(与 RunCommand 一致,会话内不能绕过)
        var br = Remot.Server.Security.CommandGuard.Check(request.Command,
            _config.BlockedCommands.Count > 0 ? _config.BlockedCommands : null,
            _config.ProtectedServices.Count > 0 ? _config.ProtectedServices : null,
            _config.ProtectedPaths.Count > 0 ? _config.ProtectedPaths : null);
        if (br is not null)
        {
            AuditLog.Log($"⛔ BLOCKED(session): {br} | cmd: {Truncate(request.Command, 100)}");
            await WriteResult(stream, 0, new CommandRunResult(-1, "", "", 0, false, br));
            return;
        }
        AuditLog.Log($"session run {Truncate(request.SessionId, 8)}: {Truncate(request.Command, 100)}");
        var r = await _sessions.RunAsync(request.SessionId, request.Command, request.TimeoutMs > 0 ? request.TimeoutMs : null, context.CancellationToken,
            onLine: async line => await stream.WriteAsync(new CommandOutput
            {
                Chunk = new StreamChunk { IsStderr = line.IsStderr, Data = Google.Protobuf.ByteString.CopyFromUtf8(line.Line + "\n") }
            }));
        await WriteResult(stream, 0, r);
    }

    public override Task<CloseSessionResponse> CloseSession(CloseSessionRequest request, ServerCallContext context)
    {
        var ok = _sessions.Close(request.SessionId);
        AuditLog.Log($"session close {Truncate(request.SessionId, 8)} ok={ok}");
        return Task.FromResult(new CloseSessionResponse { Ok = ok, Error = ok ? "" : "session 不存在" });
    }

    private static async IAsyncEnumerable<FileChunk> ReadAll(IAsyncStreamReader<FileChunk> reader, [EnumeratorCancellation] CancellationToken ct)
    {
        while (await reader.MoveNext(ct))   // M5:透传取消
            yield return reader.Current;
    }
}
