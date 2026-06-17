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
        using var _ = proc;

        var stdoutEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutBuf = new OutputAccumulator(spec.MaxOutputBytes);
        var stderrBuf = new OutputAccumulator(spec.MaxOutputBytes);

        // L4:MergeStreams 时把 stderr 并入 stdout(缓冲与流式均合并)
        void OnLine(string? line, bool isStderr)
        {
            if (line is null) { (isStderr ? stderrEof : stdoutEof).TrySetResult(); return; }
            bool asStderr = isStderr && !spec.MergeStreams;
            (asStderr ? stderrBuf : stdoutBuf).Append(line);
            if (onLine is not null)
            {
                try { onLine(new StreamLine(asStderr, line)).GetAwaiter().GetResult(); } catch { }
            }
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data, isStderr: false);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data, isStderr: true);
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
