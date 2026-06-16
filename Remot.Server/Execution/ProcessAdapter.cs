using System.Diagnostics;

namespace Remot.Server.Execution;

public sealed class ProcessAdapter : IProcessAdapter
{
    private readonly Process _p;
    private readonly JobObject? _job;   // 可为 null(测试或无权限时降级)

    private ProcessAdapter(Process p, JobObject? job) { _p = p; _job = job; }

    public int Id => _p.Id;
    public StreamWriter StandardInput => _p.StandardInput;
    public int ExitCode => _p.ExitCode;
    public bool HasExited => _p.HasExited;
    public IntPtr Handle => _p.Handle;

    public event DataReceivedEventHandler? OutputDataReceived
    { add => _p.OutputDataReceived += value; remove => _p.OutputDataReceived -= value; }
    public event DataReceivedEventHandler? ErrorDataReceived
    { add => _p.ErrorDataReceived += value; remove => _p.ErrorDataReceived -= value; }

    public void BeginOutputRead() => _p.BeginOutputReadLine();
    public void BeginErrorRead() => _p.BeginErrorReadLine();

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        // .NET 的 Process.WaitForExitAsync 只有单参数(CancellationToken)重载,
        // 这里自行组合超时:超时返回 false(仍在跑),正常退出返回 true,外部取消则抛出。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan)
            timeoutCts.CancelAfter(timeout);
        try
        {
            await _p.WaitForExitAsync(timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return _p.HasExited; // 超时(非外部取消)
        }
    }

    public void KillEntireTree()
    {
        // JobObject 关闭句柄 → 整树(含 nssm 拉起的子进程)被杀
        if (_job is not null) { _job.Dispose(); return; }
        try { _p.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        _job?.Dispose();
        _p.Dispose();
    }

    internal static ProcessAdapter Create(Process p, JobObject? job) => new(p, job);
}
