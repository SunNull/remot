using System.Diagnostics;

namespace Remot.Server.Setup;

/// <summary>把 Remot.Server 注册为 Windows 服务(自动启动 + 失败重启)并开防火墙端口。
/// install/uninstall 须以管理员身份运行(内部用 runas 触发 UAC)。</summary>
public static class ServiceInstaller
{
    public const string ServiceName = "RemotServer";

    public static void Install(string exePath, int port)
    {
        // M11:检查退出码,任一关键步骤失败即中止,避免半成品状态。
        if (Run("sc.exe", $"create {ServiceName} binPath= \"{exePath}\" start= auto") != 0)
            throw new Exception("sc create 失败(服务可能已存在或非管理员)");
        Run("sc.exe", $"description {ServiceName} \"Remot 远程执行与文件传输服务\"");
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        if (Run("netsh", $"advfirewall firewall add rule name=\"RemotServer\" dir=in action=allow protocol=TCP localport={port}") != 0)
            Console.Error.WriteLine("警告:防火墙规则添加失败(可能已存在或非管理员)");
        if (Run("sc.exe", $"start {ServiceName}") != 0)
            Console.Error.WriteLine("警告:服务启动失败(可稍后手动 sc start)");
    }

    public static void Uninstall()
    {
        Run("sc.exe", $"stop {ServiceName}");
        Run("sc.exe", $"delete {ServiceName}");
        Run("netsh", $"advfirewall firewall delete rule name=\"RemotServer\"");
    }

    /// <summary>运行命令并返回退出码;-1 表示启动失败。</summary>
    private static int Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { Verb = "runas", UseShellExecute = true, CreateNoWindow = true };
        try
        {
            var p = Process.Start(psi);
            if (p is null) { Console.Error.WriteLine($"{exe} 未能启动"); return -1; }
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{exe} {args} 失败:{ex.Message}");
            return -1;
        }
    }
}
