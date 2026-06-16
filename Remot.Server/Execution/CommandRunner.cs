using System.Diagnostics;
using System.Text;

namespace Remot.Server.Execution;

public sealed class CommandRunner : ICommandRunner
{
    private readonly IProcessFactory _factory;
    private const string TruncationMarker = "...[truncated]";

    public CommandRunner(IProcessFactory factory) => _factory = factory;

    public async Task<CommandRunResult> RunAsync(CommandSpec spec, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        IProcessAdapter proc;
        try { proc = _factory.Start(spec); }
        catch (ProcessStartException ex) { return new CommandRunResult(-1, "", "", 0, false, ex.Message); }
        using var _ = proc;   // IProcessAdapter 实现 IDisposable

        var stdoutBuf = new OutputAccumulator(spec.MaxOutputBytes);
        var stderrBuf = new OutputAccumulator(spec.MaxOutputBytes);
        proc.OutputDataReceived += (_, e) => stdoutBuf.Append(e.Data);
        proc.ErrorDataReceived  += (_, e) => stderrBuf.Append(e.Data);
        proc.BeginOutputRead();
        proc.BeginErrorRead();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (spec.TimeoutMs is int ms && ms > 0)
            linked.CancelAfter(TimeSpan.FromMilliseconds(ms));
        var timeout = spec.TimeoutMs is int t && t > 0
            ? TimeSpan.FromMilliseconds(t)
            : Timeout.InfiniteTimeSpan;

        bool timedOut = false;
        bool exited;
        try { exited = await proc.WaitForExitAsync(timeout, linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { exited = false; timedOut = true; }

        if (!exited)
        {
            timedOut = true;
            proc.KillEntireTree();
            try { await proc.WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
        }

        sw.Stop();
        return new CommandRunResult(
            ExitCode: proc.HasExited ? proc.ExitCode : -1,
            Stdout: stdoutBuf.ToString(),
            Stderr: stderrBuf.ToString(),
            DurationMs: sw.ElapsedMilliseconds,
            TimedOut: timedOut || ct.IsCancellationRequested,
            Error: null);
    }

    private sealed class OutputAccumulator(long maxBytes)
    {
        private readonly StringBuilder _sb = new();
        private long _bytes;
        private bool _capped;

        public void Append(string? line)
        {
            if (line is null || _capped) return;   // null = 流结束
            var add = Encoding.UTF8.GetByteCount(line) + 1; // +换行
            if (_bytes + add > maxBytes)
            {
                _capped = true;
                _sb.AppendLine().Append(TruncationMarker);
                return;
            }
            _bytes += add;
            _sb.AppendLine(line);
        }

        public override string ToString() => _sb.ToString();
    }
}
