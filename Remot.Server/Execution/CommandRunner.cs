using System.Diagnostics;
using System.Text;

namespace Remot.Server.Execution;

public sealed class CommandRunner : ICommandRunner
{
    private readonly IProcessFactory _factory;
    private const string TruncationMarker = "...[truncated]";
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    public CommandRunner(IProcessFactory factory) => _factory = factory;

    public async Task<CommandRunResult> RunAsync(CommandSpec spec, CancellationToken ct = default, Func<StreamLine, Task>? onLine = null)
    {
        var sw = Stopwatch.StartNew();
        IProcessAdapter proc;
        try { proc = _factory.Start(spec); }
        catch (ProcessStartException ex) { return new CommandRunResult(-1, "", "", 0, false, ex.Message); }
        using var _ = proc;   // IProcessAdapter 实现 IDisposable

        // EOF(null 回调)追踪两条流是否排空,避免进程退出后丢尾部输出。
        var stdoutEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutBuf = new OutputAccumulator(spec.MaxOutputBytes);
        var stderrBuf = new OutputAccumulator(spec.MaxOutputBytes);
        proc.OutputDataReceived += (_, e) => HandleLine(e.Data, isStderr: false, stdoutBuf, stdoutEof, onLine);
        proc.ErrorDataReceived += (_, e) => HandleLine(e.Data, isStderr: true, stderrBuf, stderrEof, onLine);
        proc.BeginOutputRead();
        proc.BeginErrorRead();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (spec.TimeoutMs is int ms && ms > 0)
            linked.CancelAfter(TimeSpan.FromMilliseconds(ms));
        var timeout = spec.TimeoutMs is int t && t > 0
            ? TimeSpan.FromMilliseconds(t)
            : Timeout.InfiniteTimeSpan;

        bool timedOut = false;
        bool cancelled = false;
        bool exited;
        try { exited = await proc.WaitForExitAsync(timeout, linked.Token); }
        catch (OperationCanceledException)
        {
            exited = false;
            cancelled = ct.IsCancellationRequested;
        }

        if (!exited)
        {
            if (!cancelled) timedOut = true;
            proc.KillEntireTree();
            try { await proc.WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
        }

        // 排空异步输出管道:进程退出后回调可能仍在路上。
        try { await Task.WhenAll(stdoutEof.Task, stderrEof.Task).WaitAsync(DrainTimeout); }
        catch { }

        sw.Stop();
        return new CommandRunResult(
            ExitCode: proc.HasExited ? proc.ExitCode : -1,
            Stdout: stdoutBuf.ToString(),
            Stderr: stderrBuf.ToString(),
            DurationMs: sw.ElapsedMilliseconds,
            TimedOut: timedOut,
            Error: cancelled ? "cancelled" : null);
    }

    private static void HandleLine(string? line, bool isStderr, OutputAccumulator buf, TaskCompletionSource eof, Func<StreamLine, Task>? onLine)
    {
        if (line is null) { eof.TrySetResult(); return; }
        buf.Append(line);
        if (onLine is not null)
        {
            // H1:实时流式。reader 线程无同步上下文,GetResult 不会死锁;客户端慢则靠超时/杀树自恢复。
            try { onLine(new StreamLine(isStderr, line)).GetAwaiter().GetResult(); } catch { }
        }
    }

    private sealed class OutputAccumulator(long maxBytes)
    {
        private readonly StringBuilder _sb = new();
        private long _bytes;
        private bool _capped;

        public void Append(string? line)
        {
            if (line is null || _capped) return;
            var add = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            if (_bytes + add > maxBytes) { _capped = true; _sb.Append(TruncationMarker); return; }
            _bytes += add;
            _sb.AppendLine(line);
        }

        public override string ToString() => _sb.ToString();
    }
}
