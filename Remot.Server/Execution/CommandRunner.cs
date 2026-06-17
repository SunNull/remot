using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

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

        // H1/M2:stdout/stderr 在不同 ThreadPool 线程上回调,若直接 await stream.WriteAsync
        // 或 Append 同一 StringBuilder 会并发(gRPC IServerStreamWriter 仅允许单写,会抛异常/帧交错)。
        // 用一个 Channel 把所有行汇到单消费者顺序处理,彻底消除并发写入。
        var channel = Channel.CreateUnbounded<StreamLine>(new UnboundedChannelOptions { SingleReader = true });
        var consumer = Task.Run(async () =>
        {
            await foreach (var line in channel.Reader.ReadAllAsync())
            {
                (line.IsStderr ? stderrBuf : stdoutBuf).Append(line.Line);
                if (onLine is not null)
                    try { await onLine(line); } catch { }
            }
        });

        void OnLine(string? data, bool isStderr)
        {
            if (data is null) { (isStderr ? stderrEof : stdoutEof).TrySetResult(); return; }
            channel.Writer.TryWrite(new StreamLine(isStderr && !spec.MergeStreams, data));
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

        // 排空异步输出管道:进程退出后,OutputDataReceived 回调可能仍在路上。
        try { await Task.WhenAll(stdoutEof.Task, stderrEof.Task).WaitAsync(DrainTimeout); }
        catch { }

        // 关闭 Channel,等单消费者处理完剩余行后退出 —— 此时无并发,stream 写入安全。
        channel.Writer.TryComplete();
        try { await consumer.WaitAsync(DrainTimeout); } catch { }

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
