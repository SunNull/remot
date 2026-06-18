using Remot.Server.Execution;
using Xunit;

namespace Remot.Server.Tests;

public class ShellSessionCmdTests
{
    [Fact]
    public async Task Cmd_session_diagnostic()
    {
        // 不断言,只看 ShellSession 收到了什么
        using var session = new ShellSession("cmd", null);
        var r = await session.RunAsync("echo hello-cmd", 5000, default, null);
        // 输出诊断信息
        var msg = $"ExitCode={r.ExitCode} TimedOut={r.TimedOut} Error=[{r.Error}] Stdout=[{r.Stdout}] Stderr=[{r.Stderr}]";
        Assert.True(r.ExitCode == 0 || r.Stdout.Contains("hello-cmd"), msg);
    }

    [Fact]
    public async Task PS_session_comparison()
    {
        // PowerShell 对照(确认 ShellSession 本身没问题)
        var shell = ShellDetector.Preferred;
        using var session = new ShellSession(shell, null);
        var r = await session.RunAsync("echo 'hello-ps'", 5000, default, null);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("hello-ps", r.Stdout);
    }
}
