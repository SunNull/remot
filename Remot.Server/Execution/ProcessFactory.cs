using System.Diagnostics;
using System.Text;

namespace Remot.Server.Execution;

public sealed class ProcessFactory : IProcessFactory
{
    private static readonly HashSet<string> AllowedShells =
        new(StringComparer.OrdinalIgnoreCase) { "pwsh", "powershell", "cmd" };

    public IProcessAdapter Start(CommandSpec spec)
    {
        var psi = BuildStartInfo(spec);
        var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
            p.StandardInput.Close();   // H2:立即关闭 stdin,避免会读 stdin 的命令(cmd /C more、python 交互等)永久挂起
            // L3 说明:Start 与 Assign 间存在极小竞态窗口(理论上孙进程可能在挂入 job 前逃逸)。
            // 完整修复需 CREATE_SUSPENDED + P/Invoke CreateProcess,代价大;当前「启动后立即挂入 job」
            // + KillEntireTree 的 entireProcessTree 回退已把实际风险降到极低,按权衡保留该残余。
            JobObject? job = null;
            JobObject? jo = null;
            try
            {
                jo = new JobObject();   // M3:构造放进 try,非 Windows/无权限时降级,而非让整个 Start 失败
                if (!jo.Assign(p.Handle)) { jo.Dispose(); jo = null; }   // M2:Assign 失败时 Dispose,避免内核句柄泄漏
                else job = jo;
            }
            catch { jo?.Dispose(); }
            return ProcessAdapter.Create(p, job);
        }
        catch (Exception ex)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            p.Dispose();
            throw new ProcessStartException(spec.Shell, ex);
        }
    }

    private static ProcessStartInfo BuildStartInfo(CommandSpec spec)
    {
        var shell = ShellDetector.Resolve(spec.Shell).ToLowerInvariant();   // 优化1:空/auto → pwsh 优先
        if (!AllowedShells.Contains(shell))   // H9:白名单,未知 shell 显式拒绝
            throw new ArgumentException($"不支持的 shell: {shell}(允许: pwsh/powershell/cmd,空/auto=自动)");

        var (fileName, args) = shell switch
        {
            // 前置把控制台输出切到 UTF-8,否则中文 Windows 下子进程默认 GBK 会乱码。
            "cmd"        => ("cmd.exe", "/S /C " + Quote("chcp 65001 >nul && " + spec.Text)),
            "powershell" => ("powershell.exe", "-NoProfile -NonInteractive -Command " + Quote("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + spec.Text)),
            _            => ("pwsh.exe", "-NoProfile -NonInteractive -Command " + Quote("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + spec.Text)),
        };

        var psi = new ProcessStartInfo(fileName, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrEmpty(spec.Cwd) ? Environment.CurrentDirectory : spec.Cwd
        };
        foreach (var (k, v) in spec.Env ?? new Dictionary<string, string>())
            psi.Environment[k] = v;
        // L7:仅在用户未设置时默认 UTF-8,不再静默覆盖。
        if (!psi.Environment.ContainsKey("PYTHONIOENCODING"))
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
        return psi;
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}

public sealed class ProcessStartException : Exception
{
    public ProcessStartException(string shell, Exception inner) : base($"无法启动 shell: {shell}", inner) { }
}
