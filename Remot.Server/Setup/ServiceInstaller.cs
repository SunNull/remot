using System.Diagnostics;

namespace Remot.Server.Setup;

/// <summary>把 Remot.Server 注册为 Windows 服务(自动启动 + 失败重启)并开防火墙端口。
/// install/uninstall 须以管理员身份运行(内部用 runas 触发 UAC)。</summary>
public static class ServiceInstaller
{
    public const string ServiceName = "RemotServer";

    public static void Install(string exePath, int port)
    {
        Run("sc.exe", $"create {ServiceName} binPath= \"{exePath}\" start= auto");
        Run("sc.exe", $"description {ServiceName} \"Remot 远程执行与文件传输服务\"");
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        NetshAllow(port);
        Run("sc.exe", $"start {ServiceName}");
    }

    public static void Uninstall()
    {
        Run("sc.exe", $"stop {ServiceName}");
        Run("sc.exe", $"delete {ServiceName}");
        NetshDelete();
    }

    private static void NetshAllow(int port) =>
        Run("netsh", $"advfirewall firewall add rule name=\"RemotServer\" dir=in action=allow protocol=TCP localport={port}");

    private static void NetshDelete() =>
        Run("netsh", $"advfirewall firewall delete rule name=\"RemotServer\"");

    private static void Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { Verb = "runas", UseShellExecute = true, CreateNoWindow = true };
        try { Process.Start(psi)?.WaitForExit(); }
        catch (Exception ex) { Console.Error.WriteLine($"{exe} {args} 失败:{ex.Message}"); }
    }
}
