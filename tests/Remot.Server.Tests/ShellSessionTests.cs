using Remot.Server.Execution;
using Xunit;

namespace Remot.Server.Tests;

/// <summary>优化3:持久 shell 会话(真实 powershell/pwsh 进程,非 fake)。</summary>
public class ShellSessionTests
{
    private static string Shell => ShellDetector.Preferred;

    [Fact]
    public async Task Runs_multiple_commands_in_one_process()
    {
        using var session = new ShellSession(Shell, null);
        var r1 = await session.RunAsync("Write-Output 'first'", null, default, null);
        var r2 = await session.RunAsync("Write-Output 'second'", null, default, null);
        Assert.Equal(0, r1.ExitCode);
        Assert.Contains("first", r1.Stdout);
        Assert.Equal(0, r2.ExitCode);
        Assert.Contains("second", r2.Stdout);
    }

    [Fact]
    public async Task State_persists_across_commands()   // 同进程:全局变量保持
    {
        using var session = new ShellSession(Shell, null);
        await session.RunAsync("$global:remot_marker = 12345", null, default, null);
        var r = await session.RunAsync("Write-Output $global:remot_marker", null, default, null);
        Assert.Contains("12345", r.Stdout);
    }

    [Fact]
    public async Task Captures_nonzero_exit_on_error()
    {
        using var session = new ShellSession(Shell, null);
        var fail = await session.RunAsync("Write-Error 'boom-marker'", null, default, null);
        Assert.NotEqual(0, fail.ExitCode);
        Assert.Contains("boom-marker", fail.Stderr);
    }
}
