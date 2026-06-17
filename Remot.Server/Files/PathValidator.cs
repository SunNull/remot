namespace Remot.Server.Files;

/// <summary>路径安全校验:规范化、拒绝穿越;若配置了允许基目录则强制限定其下(C1/C2 缓解)。</summary>
public static class PathValidator
{
    /// <summary>校验并返回规范化的绝对路径;非法或越界抛异常。</summary>
    public static string Validate(string? raw, IReadOnlyList<string> allowedBasePaths)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("路径为空");

        string full;
        try { full = Path.GetFullPath(raw); }
        catch (Exception ex) { throw new ArgumentException($"非法路径: {raw}", ex); }

        if (allowedBasePaths.Count > 0)
        {
            bool allowed = false;
            foreach (var b in allowedBasePaths)
            {
                if (string.IsNullOrWhiteSpace(b)) continue;
                string baseFull;
                try { baseFull = Path.GetFullPath(b); }
                catch { continue; }
                if (full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase) &&
                    (full.Length == baseFull.Length ||
                     full[baseFull.Length] == Path.DirectorySeparatorChar ||
                     full[baseFull.Length] == Path.AltDirectorySeparatorChar))
                {
                    allowed = true;
                    break;
                }
            }
            if (!allowed)
                throw new UnauthorizedAccessException($"路径不在允许范围内: {raw}");
        }
        return full;
    }
}
