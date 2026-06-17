using System.Diagnostics;

namespace Remot.Server.Execution;

/// <summary>探测可用的最优 shell:pwsh 优先(启动 ~50ms vs powershell ~300ms,且默认 UTF-8 无中文乱码),
/// 未安装则降级 powershell(Windows 必装)。</summary>
internal static class ShellDetector
{
    private static readonly string _preferred = Detect();
    public static string Preferred => _preferred;

    /// <summary>解析 shell:空/"auto" → 探测结果(pwsh 或 powershell);其余原样返回(白名单由调用方校验)。</summary>
    public static string Resolve(string? shell) =>
        string.IsNullOrEmpty(shell) || shell.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? _preferred
            : shell;

    private static string Detect()
    {
        try
        {
            var psi = new ProcessStartInfo("pwsh", "--version")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi);
            if (p is not null && p.WaitForExit(3000) && p.ExitCode == 0) return "pwsh";
        }
        catch { /* pwsh 未安装或不可用,降级 */ }
        return "powershell";
    }
}
