namespace Remot.Server;

/// <summary>M10:简易审计日志 + 配对串落盘(Windows 服务态下不丢失关键信息)。
/// 写入 %ProgramData%\Remot\,权限收紧(复用 FileProtection)。失败静默(不阻塞主流程)。</summary>
internal static class AuditLog
{
    private const long MaxLogBytes = 10 * 1024 * 1024; // 10MB 轮转阈值(L9)
    private static readonly object _lock = new();

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
        lock (_lock)   // M5:串行化,避免并发 AppendAllText 共享冲突导致审计行丢失
        {
            try
            {
                var p = Path.Combine(Dir, "audit.log");
                // L9:超阈值轮转,避免长期运行无限增长
                if (File.Exists(p) && new FileInfo(p).Length > MaxLogBytes)
                { try { File.Move(p, p + ".1", overwrite: true); } catch { } }
                bool isNew = !File.Exists(p);
                File.AppendAllText(p, $"{DateTime.Now:O}\t{what}{Environment.NewLine}");
                if (isNew) FileProtection.Restrict(p);
            }
            catch { }
        }
    }
}
