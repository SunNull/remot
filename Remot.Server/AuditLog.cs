namespace Remot.Server;

/// <summary>M10:简易审计日志 + 配对串落盘(Windows 服务态下不丢失关键信息)。
/// 写入 %ProgramData%\Remot\,权限收紧(复用 FileProtection)。失败静默(不阻塞主流程)。</summary>
internal static class AuditLog
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Remot");

    /// <summary>把首启配对串写入 pairing.txt(服务态无控制台,可从这里取回)。</summary>
    public static void SavePairing(string pairingString)
    {
        try
        {
            var p = Path.Combine(Dir, "pairing.txt");
            File.WriteAllText(p, pairingString + Environment.NewLine);
            FileProtection.Restrict(p);
        }
        catch { }
    }

    /// <summary>审计一行(调用方/命令/时间)。</summary>
    public static void Log(string what)
    {
        try
        {
            var p = Path.Combine(Dir, "audit.log");
            bool isNew = !File.Exists(p);
            File.AppendAllText(p, $"{DateTime.Now:O}\t{what}{Environment.NewLine}");
            if (isNew) FileProtection.Restrict(p);
        }
        catch { }
    }
}
