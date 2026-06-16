using System.Diagnostics;
using System.Reflection;
using Remot.Server.Execution;

namespace Remot.Server.Tests.Fakes;

internal sealed class FakeProcess : IProcessAdapter
{
    // 由测试显式 Complete() 来表示进程退出,避免基于时间的竞态。
    private readonly TaskCompletionSource _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id { get; set; } = 12345;
    public int ExitCode { get; set; } = 0;
    public bool HasExited { get; private set; }
    public bool TreeKilled { get; private set; }
    public bool NeverExits { get; set; }   // 超时测试用:永不自行退出

    public StreamWriter StandardInput { get; } = new StreamWriter(Stream.Null);
    public IntPtr Handle { get; } = IntPtr.Zero;

    public event DataReceivedEventHandler? OutputDataReceived;
    public event DataReceivedEventHandler? ErrorDataReceived;

    public void EmitStdout(string line) => OutputDataReceived?.Invoke(this, Args(line));
    public void EmitStderr(string line) => ErrorDataReceived?.Invoke(this, Args(line));
    public void Complete() { if (!NeverExits) _done.TrySetResult(); }

    public void BeginOutputRead() { }
    public void BeginErrorRead() { }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _done.Task.WaitAsync(timeout, ct);
            HasExited = true;
            return true;
        }
        catch (TimeoutException) { return HasExited; }   // 仅超时:返回 false(仍在跑)
        // ct 取消 → OperationCanceledException 自然向上抛
    }

    public void KillEntireTree() { TreeKilled = true; HasExited = true; _done.TrySetResult(); }
    public void Dispose() { }

    // DataReceivedEventArgs 无公开构造,用反射造一个。
    private static DataReceivedEventArgs Args(string line)
    {
        var ctor = typeof(DataReceivedEventArgs)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0];
        return (DataReceivedEventArgs)ctor.Invoke(new object?[] { line });
    }
}

internal sealed class FakeFactory : IProcessFactory
{
    public FakeProcess Process { get; } = new();
    public bool ThrowOnStart { get; set; }
    public IProcessAdapter Start(CommandSpec spec)
    {
        if (ThrowOnStart) throw new ProcessStartException("pwsh", new InvalidOperationException("boom"));
        return Process;
    }
}
