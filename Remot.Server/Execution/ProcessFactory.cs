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
            JobObject? job = null;
            var jo = new JobObject();
            try
            {
                if (!jo.Assign(p.Handle)) { jo.Dispose(); }   // M2:Assign 失败时 Dispose,避免内核句柄泄漏
                else job = jo;
            }
            catch { jo.Dispose(); }
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
        var shell = spec.Shell.ToLowerInvariant();
        if (!AllowedShells.Contains(shell))   // H9:白名单,未知 shell 显式拒绝
            throw new ArgumentException($"不支持的 shell: {spec.Shell}(允许: pwsh/powershell/cmd)");

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
