using System.Text.RegularExpressions;

namespace Remot.Server.Security;

/// <summary>危险命令拦截(方案 A:黑名单 + 服务/路径保护)。</summary>
public static class CommandGuard
{
    /// <summary>预配置黑名单(开箱即用,不可关闭)。</summary>
    private static readonly (Regex Pattern, string Reason)[] HardBlocked =
    {
        // 关机/重启
        (New(@"\bshutdown\b"), "关机/重启"),
        (New(@"\brestart-computer\b"), "重启计算机"),
        (New(@"\bstop-computer\b"), "关机"),
        // 格式化
        (New(@"\bformat\b\s+[a-z]:"), "格式化磁盘"),
        // 删注册表
        (New(@"\breg\s+delete\b"), "删除注册表"),
        (New(@"\bremove-item.*HKLM:\\\b"), "删除注册表"),
        (New(@"\bremove-item.*HKCU:\\\b"), "删除注册表"),
        // 改用户/密码
        (New(@"\bnet\s+user\b.*\S+\s+\S"), "修改用户密码"),
        (New(@"\bnet\s+localgroup\b"), "操作用户组"),
        // 操作 Remot 自身
        (New(@"\bsc\s+(delete|stop|config)\s+RemotServer\b", ignoreCase: true), "操作 Remot 服务"),
        (New(@"\bstop-service\s+.*RemotServer\b"), "停止 Remot 服务"),
        (New(@"\bremove-service\s+.*RemotServer\b"), "删除 Remot 服务"),
        // 删服务(通用)
        (New(@"\bsc\s+delete\b"), "删除 Windows 服务"),
        (New(@"\bremove-service\b"), "删除服务"),
        // 磁盘清理/高危
        (New(@"\bdiskpart\b"), "磁盘分区操作"),
        (New(@"\bbcdedit\b"), "修改启动配置"),
        // 批量递归删除根目录
        (New(@"\bdel\s+/[sSq].*\\\*"), "批量删除(递归通配)"),
        (New(@"\brmdir\s+/[sS].*\\\*"), "递归删除目录"),
        (New(@"\bremove-item\s+.*-recurse.*\\\*"), "递归删除目录"),
        // 远程下载执行
        (New(@"\biex\b.*\binvoke-webrequest\b"), "远程下载并执行"),
        (New(@"\biex\b.*\birm\b"), "远程下载并执行"),
        (New(@"\bcurl\b.*\|\s*(bash|sh|pwsh|powershell)"), "远程下载管道执行"),
        // 计划任务植入
        (New(@"\bschtasks\s+/create\b"), "创建计划任务"),
        (New(@"\bregister-scheduledtask\b"), "创建计划任务"),
        // 注册 DLL / 安装服务
        (New(@"\bregsvr32\b"), "注册 DLL"),
        (New(@"\bsc\s+create\b"), "创建服务"),
    };

    /// <summary>服务保护名单(默认含 RemotServer;可在 server.json 扩展)。</summary>
    private static readonly string[] DefaultProtectedServices = { "RemotServer" };

    /// <summary>路径保护(默认系统关键目录)。</summary>
    private static readonly string[] DefaultProtectedPaths =
    {
        @"C:\Windows\System32",
        @"C:\Windows\SysWOW64",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData\Remot",
    };

    /// <summary>检查命令是否安全。返回 null=放行;否则返回拦截原因。</summary>
    public static string? Check(string command, IReadOnlyList<string>? extraProtectedServices = null, IReadOnlyList<string>? extraProtectedPaths = null, IReadOnlyList<string>? extraBlockedPatterns = null)
    {
        // 0. 用户自定义黑名单(server.json 的 BlockedCommands)
        if (extraBlockedPatterns is not null)
        {
            foreach (var p in extraBlockedPatterns)
            {
                if (!string.IsNullOrWhiteSpace(p))
                {
                    try
                    {
                        if (Regex.IsMatch(command, p, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                            return $"⛔ 拦截(自定义规则 '{p}'):命令匹配用户黑名单";
                    }
                    catch { /* 无效正则:跳过 */ }
                }
            }
        }

        // 1. 内置黑名单(不可关闭)
        foreach (var (pattern, reason) in HardBlocked)
            if (pattern.IsMatch(command))
                return $"⛔ 拦截({reason}):命令匹配危险模式";

        // 2. 服务保护(任一被 stop/delete)
        var services = new HashSet<string>(DefaultProtectedServices, StringComparer.OrdinalIgnoreCase);
        if (extraProtectedServices is not null)
            foreach (var s in extraProtectedServices) services.Add(s);

        var svcMatch = Regex.Match(command, @"\b(?:sc\s+(?:stop|delete|config)|stop-service)\s+([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
        if (svcMatch.Success)
        {
            var svc = svcMatch.Groups[1].Value;
            if (services.Contains(svc))
                return $"⛔ 拦截:服务 '{svc}' 受保护";
        }

        // 3. 路径保护(del/rmdir/remove-item 指向受保护目录)
        var paths = new List<string>(DefaultProtectedPaths);
        if (extraProtectedPaths is not null) paths.AddRange(extraProtectedPaths);

        var delMatch = Regex.Match(command, @"(?i)(?:del|rmdir|remove-item|erase)[^A-Za-z]*(C:\\[^""\s|&>]+)");
        if (delMatch.Success)
        {
            var target = delMatch.Groups[1].Value;
            foreach (var p in paths)
                if (target.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return $"⛔ 拦截:路径 '{p}' 受保护,禁止删除";
        }

        return null; // 放行
    }

    private static Regex New(string pattern, bool ignoreCase = true)
        => new(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromMilliseconds(100));
}
