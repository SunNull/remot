using System.Diagnostics;
using System.Text;

namespace Remot.Server.Execution;

public sealed class ProcessFactory : IProcessFactory
{
    public IProcessAdapter Start(CommandSpec spec)
    {
        var psi = BuildStartInfo(spec);
        var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
            JobObject? job = null;
            try { job = new JobObject(); if (!job.Assign(p.Handle)) job = null; }
            catch { /* 无权限降级:KillEntireTree 回退到 entireProcessTree */ job = null; }
            return ProcessAdapter.Create(p, job);   // 成功:所有权转移给 ProcessAdapter
        }
        catch (Exception ex)
        {
            // 启动后若异常,清理已启动的进程,避免孤儿。
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            p.Dispose();
            throw new ProcessStartException(spec.Shell, ex);
        }
    }

    private static ProcessStartInfo BuildStartInfo(CommandSpec spec)
    {
        var (fileName, args) = spec.Shell.ToLowerInvariant() switch
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
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        return psi;
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}

public sealed class ProcessStartException : Exception
{
    public ProcessStartException(string shell, Exception inner) : base($"无法启动 shell: {shell}", inner) { }
}
