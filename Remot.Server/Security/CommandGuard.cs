using System.Text.RegularExpressions;

namespace Remot.Server.Security;

/// <summary>危险命令拦截。所有规则来自 server.json 配置,无内置硬编码。</summary>
public static class CommandGuard
{
    /// <summary>检查命令是否安全。返回 null=放行;否则返回拦截原因。</summary>
    public static string? Check(string command, IReadOnlyList<string>? blockedCommands = null, IReadOnlyList<string>? protectedServices = null, IReadOnlyList<string>? protectedPaths = null)
    {
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

        // 2. 服务保护
        if (protectedServices is not null && protectedServices.Count > 0)
        {
            var svcMatch = Regex.Match(command, @"\b(?:sc\s+(?:stop|delete|config)|stop-service)\s+([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            if (svcMatch.Success)
            {
                var svc = svcMatch.Groups[1].Value;
                if (protectedServices.Any(s => s.Equals(svc, StringComparison.OrdinalIgnoreCase)))
                    return $"⛔ 拦截:服务 '{svc}' 受保护";
            }
        }

        // 3. 路径保护
        if (protectedPaths is not null && protectedPaths.Count > 0)
        {
            var delMatch = Regex.Match(command, @"(?i)(?:del|rmdir|remove-item|erase)[^A-Za-z]*(C:\\[^""\s|&>]+|D:\\[^""\s|&>]+|E:\\[^""\s|&>]+|F:\\[^""\s|&>]+)", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (delMatch.Success)
            {
                var target = delMatch.Groups[1].Value;
                foreach (var p in protectedPaths)
                    if (target.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        return $"⛔ 拦截:路径 '{p}' 受保护";
            }
        }

        return null;
    }
}
