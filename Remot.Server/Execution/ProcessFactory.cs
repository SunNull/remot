using System.Diagnostics;
using System.Text;

namespace Remot.Server.Execution;

public sealed class ProcessFactory : IProcessFactory
{
    public IProcessAdapter Start(CommandSpec spec)
    {
        var psi = BuildStartInfo(spec);
        try
        {
            var p = new Process { StartInfo = psi };
            p.Start();
            JobObject? job = null;
            try { job = new JobObject(); job.Assign(p.Handle); }
            catch { /* 无权限降级:KillEntireTree 回退到 entireProcessTree */ }
            return ProcessAdapter.Create(p, job);
        }
        catch (Exception ex)
        {
            throw new ProcessStartException(spec.Shell, ex);
        }
    }

    private static ProcessStartInfo BuildStartInfo(CommandSpec spec)
    {
        var (fileName, args) = spec.Shell.ToLowerInvariant() switch
        {
            "cmd"       => ("cmd.exe", "/S /C " + Quote(spec.Text)),
            "powershell" => ("powershell.exe", "-NoProfile -NonInteractive -Command " + Quote(spec.Text)),
            _           => ("pwsh.exe", "-NoProfile -NonInteractive -Command " + Quote(spec.Text)),
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
