using System.Diagnostics;
using System.Text;

namespace Remot.Server.Execution;

public sealed class CommandRunner : ICommandRunner
{
    private readonly IProcessFactory _factory;
    private const string TruncationMarker = "...[truncated]";
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    public CommandRunner(IProcessFactory factory) => _factory = factory;

    public async Task<CommandRunResult> RunAsync(CommandSpec spec, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        IProcessAdapter proc;
        try { proc = _factory.Start(spec); }
        catch (ProcessStartException ex) { return new CommandRunResult(-1, "", "", 0, false, ex.Message); }
        using var _ = proc;   // IProcessAdapter 实现 IDisposable

        // 用 EOF(null 回调)追踪两条流是否排空,避免进程退出后丢尾部输出。
        var stdoutEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutBuf = new OutputAccumulator(spec.MaxOutputBytes);
        var stderrBuf = new OutputAccumulator(spec.MaxOutputBytes);
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) stdoutEof.TrySetResult();
            else stdoutBuf.Append(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) stderrEof.TrySetResult();
            else stderrBuf.Append(e.Data);
        };
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
            cancelled = ct.IsCancellationRequested;   // 外部取消 vs 超时
        }

        // 未正常退出 → 杀整树,确保不留孤儿(含 nssm 拉起的子服务)。
        if (!exited)
        {
            if (!cancelled) timedOut = true;           // 未退出且非外部取消 = 超时
            proc.KillEntireTree();
            try { await proc.WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
        }

        // 排空异步输出管道:进程退出后,OutputDataReceived 回调可能仍在路上。
        try { await Task.WhenAll(stdoutEof.Task, stderrEof.Task).WaitAsync(DrainTimeout); }
        catch { /* 排空超时:尽力而为 */ }

        sw.Stop();
        return new CommandRunResult(
            ExitCode: proc.HasExited ? proc.ExitCode : -1,
            Stdout: stdoutBuf.ToString(),
            Stderr: stderrBuf.ToString(),
            DurationMs: sw.ElapsedMilliseconds,
            TimedOut: timedOut,
            Error: cancelled ? "cancelled" : null);
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
            if (_bytes + add > maxBytes)
            {
                _capped = true;
                _sb.Append(TruncationMarker);
                return;
            }
            _bytes += add;
            _sb.AppendLine(line);
        }

        public override string ToString() => _sb.ToString();
    }
}
