using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Remot.Server.Execution;

public sealed class CommandRunner : ICommandRunner
{
    private readonly IProcessFactory _factory;
    private const string TruncationMarker = "...[truncated]";
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);
    private static readonly Regex SentinelRegexObj = new(@"^__REMOT_END_(\d+)__:(-?\d+)\s*$", RegexOptions.Compiled);

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

        // H1/M2:stdout/stderr 在不同 ThreadPool 线程回调,用 Channel 汇到单消费者顺序处理,消除并发写。
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
        var timeout = spec.TimeoutMs is int t && t > 0 ? TimeSpan.FromMilliseconds(t) : Timeout.InfiniteTimeSpan;

        bool timedOut = false, cancelled = false, exited;
        try { exited = await proc.WaitForExitAsync(timeout, linked.Token); }
        catch (OperationCanceledException) { exited = false; cancelled = ct.IsCancellationRequested; }

        if (!exited)
        {
            if (!cancelled) timedOut = true;
            proc.KillEntireTree();
            try { await proc.WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
        }

        try { await Task.WhenAll(stdoutEof.Task, stderrEof.Task).WaitAsync(DrainTimeout); } catch { }
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

    /// <summary>优化2:多条命令拼成一个脚本,单进程顺序执行,sentinel 切分每条结果。仅 PowerShell。</summary>
    public async Task<IReadOnlyList<CommandRunResult>> RunBatchAsync(
        IReadOnlyList<CommandSpec> specs, CancellationToken ct = default, Func<int, StreamLine, Task>? onLine = null)
    {
        if (specs.Count == 0) return Array.Empty<CommandRunResult>();
        var sw = Stopwatch.StartNew();
        var first = specs[0];
        var batchSpec = first with { Text = BuildBatchScript(specs), MergeStreams = true };

        IProcessAdapter proc;
        try { proc = _factory.Start(batchSpec); }
        catch (ProcessStartException ex)
        {
            return specs.Select((_, i) => new CommandRunResult(-1, "", "", 0, false, i == 0 ? ex.Message : "批处理启动失败,未执行")).ToList();
        }
        using var _ = proc;

        // BUG-1:channel 加 IsStderr 标记,不再丢失 stderr
        var outBufs = specs.Select(_ => new OutputAccumulator(first.MaxOutputBytes)).ToList();
        var errBufs = specs.Select(_ => new OutputAccumulator(first.MaxOutputBytes)).ToList();
        var codes = new int[specs.Count];
        Array.Fill(codes, -1);
        var stdoutEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var channel = Channel.CreateUnbounded<(int Idx, bool IsStderr, string Line)>(new UnboundedChannelOptions { SingleReader = true });
        int current = 0;
        var consumer = Task.Run(async () =>
        {
            await foreach (var (idx, isStderr, line) in channel.Reader.ReadAllAsync())
            {
                (isStderr ? errBufs[idx] : outBufs[idx]).Append(line);
                if (onLine is not null)
                    try { await onLine(idx, new StreamLine(isStderr, line)); } catch { }
            }
        });

        void OnLine(string? data, bool isStderr)
        {
            if (data is null) { (isStderr ? stderrEof : stdoutEof).TrySetResult(); return; }
            // BUG-5:sentinel 只在 stdout 检查,stderr 不检查(避免误匹配)
            if (!isStderr)
            {
                var m = SentinelRegexObj.Match(data);
                if (m.Success)
                {
                    if (int.TryParse(m.Groups[1].Value, out var i) && i >= 0 && i < codes.Length)
                        codes[i] = int.TryParse(m.Groups[2].Value, out var c) ? c : -1;
                    current = i + 1;
                    return;
                }
            }
            var idx = Math.Min(current, specs.Count - 1);
            channel.Writer.TryWrite((idx, isStderr, data));
        }
        proc.OutputDataReceived += (_, e) => OnLine(e.Data, isStderr: false);
        proc.ErrorDataReceived += (_, e) => OnLine(e.Data, isStderr: true);
        proc.BeginOutputRead();
        proc.BeginErrorRead();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (first.TimeoutMs is int ms && ms > 0)
            linked.CancelAfter(TimeSpan.FromMilliseconds(ms));
        var timeout = first.TimeoutMs is int t && t > 0 ? TimeSpan.FromMilliseconds(t) : Timeout.InfiniteTimeSpan;

        bool timedOut = false, cancelled = false, exited;
        try { exited = await proc.WaitForExitAsync(timeout, linked.Token); }
        catch (OperationCanceledException) { exited = false; cancelled = ct.IsCancellationRequested; }

        if (!exited)
        {
            if (!cancelled) timedOut = true;
            proc.KillEntireTree();
            try { await proc.WaitForExitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
        }

        try { await Task.WhenAll(stdoutEof.Task, stderrEof.Task).WaitAsync(DrainTimeout); } catch { }
        channel.Writer.TryComplete();
        try { await consumer.WaitAsync(DrainTimeout); } catch { }

        sw.Stop();
        return specs.Select((_, i) => new CommandRunResult(
            ExitCode: codes[i] != -1 ? codes[i] : (proc.HasExited ? proc.ExitCode : -1),
            Stdout: outBufs[i].ToString(),
            Stderr: errBufs[i].ToString(),
            DurationMs: sw.ElapsedMilliseconds,
            TimedOut: timedOut,
            Error: cancelled ? "cancelled" : null)).ToList();
    }

    /// <summary>构造批量脚本:每条命令后跟 sentinel 行(退出码)。PowerShell 用单引号避免 -Command 外层转义。</summary>
    private static string BuildBatchScript(IReadOnlyList<CommandSpec> specs)
    {
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference='Continue'");
        for (int i = 0; i < specs.Count; i++)
        {
            sb.Append("; $LASTEXITCODE = $null");   // 重置,避免上条外部程序退出码污染本条判断
            sb.Append("; ").Append(specs[i].Text);
            sb.Append("; $remot_ec = if (-not $?) { 1 } elseif ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }");
            sb.Append("; Write-Output ('__REMOT_END_").Append(i).Append("__:' + $remot_ec)");
        }
        return sb.ToString();
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
