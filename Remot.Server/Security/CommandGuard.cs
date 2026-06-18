using System.Text.RegularExpressions;

namespace Remot.Server.Security;

/// <summary>危险命令拦截。基于命令文本的提示性防护 —— 不是安全边界。
/// 对 PowerShell 等图灵完备 shell,任何基于正则的黑名单都可通过别名/变量/编码/管道绕过。
/// 真正的安全边界是 Token + 网络层;本类仅用于拦截"明显的高危操作"以防误操作。</summary>
public static class CommandGuard
{
    /// <summary>检查命令是否安全。返回 null=放行;否则返回拦截原因。</summary>
    public static string? Check(string command, IReadOnlyList<string>? blockedCommands = null,
        IReadOnlyList<string>? protectedServices = null, IReadOnlyList<string>? protectedPaths = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        // 1. 正则黑名单(全部来自 server.json 的 BlockedCommands)
        if (blockedCommands is not null)
        {
            foreach (var p in blockedCommands)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    if (Regex.IsMatch(command, p, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                        return $"⛔ 拦截(规则 '{p}')";
                }
                catch { }
            }
        }

        // 2. 服务保护:覆盖 sc/sc.exe、net stop、stop-service、Set-Service -Status Stopped、(Get-Service X).Stop()
        if (protectedServices is not null && protectedServices.Count > 0)
        {
            foreach (var svc in protectedServices)
            {
                if (string.IsNullOrWhiteSpace(svc)) continue;
                var name = Regex.Escape(svc);
                // H2:补 sc.exe / net stop / Set-Service / Get-Service().Stop() 等绕过路径
                var pattern =
                    $@"(?i)(?:sc(?:\.exe)?\s+(?:stop|delete|config)\s+{name}\b" +
                    $@"|net\s+stop\s+{name}\b" +
                    $@"|stop-service\s+.*?{name}\b" +
                    $@"|set-service\s+{name}\s+.*?-status\s+(?:stopped|paused)" +
                    $@"|get-service\s+{name}[^)]*\)\s*\.\s*stop\s*\()";
                try
                {
                    if (Regex.IsMatch(command, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)))
                        return $"⛔ 拦截:服务 '{svc}' 受保护";
                }
                catch { }
            }
        }

        // 3. 路径保护:删除类命令中出现受保护路径(正斜杠统一后前缀匹配,覆盖 -Path/正斜杠/多盘符)
        if (protectedPaths is not null && protectedPaths.Count > 0)
        {
            bool isDelete = false;
            try
            {
                isDelete = Regex.IsMatch(command, @"\b(del|erase|rmdir|rd|remove-item|ri|clear-item|format)\b",
                    RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            }
            catch { }
            if (isDelete)
            {
                var normalized = command.Replace('/', '\\');
                foreach (var p in protectedPaths)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    var pn = p.Replace('/', '\\');
                    // BUG-6:Contains + 边界检查(匹配位置前不能是字母/数字/:\,防子串误匹配)
                    int pos = 0;
                    while ((pos = normalized.IndexOf(pn, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        bool okBefore = pos == 0 ||
                            (!char.IsLetterOrDigit(normalized[pos - 1]) &&
                             normalized[pos - 1] != '\\' && normalized[pos - 1] != ':');
                        int after = pos + pn.Length;
                        bool okAfter = after >= normalized.Length ||
                            normalized[after] == '\\' || normalized[after] == ':' || normalized[after] == '"';
                        if (okBefore && okAfter)
                            return $"⛔ 拦截:路径 '{p}' 受保护";
                        pos++;
                    }
                }
            }
        }

        return null;
    }
}
