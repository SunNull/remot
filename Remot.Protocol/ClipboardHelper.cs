using System.Diagnostics;

namespace Remot.Protocol;

/// <summary>剪贴板读写(Windows,经 powershell,兼容 MTA 线程)。</summary>
public static class ClipboardHelper
{
    public static void SetText(string text)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var escaped = text.Replace("'", "''");
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -Command \"Set-Clipboard -Value '{escaped}'\"")
            { CreateNoWindow = true, UseShellExecute = false };
            Process.Start(psi)?.WaitForExit(3000);
        }
        catch { }
    }

    public static string? GetText()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"Get-Clipboard -Raw\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            var p = Process.Start(psi);
            if (p is null) return null;
            p.WaitForExit(3000);
            return p.StandardOutput.ReadToEnd().Trim();
        }
        catch { return null; }
    }
}
