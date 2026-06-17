using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Remot.Server.Config;

public sealed class ServerConfig
{
    private const int MinTokenLen = 32;

    public int Port { get; set; } = 7070;
    public string BindAddress { get; set; } = "0.0.0.0";      // H3:可配,默认全网卡;生产建议内网地址
    public string Token { get; set; } = "";
    public string CertPath { get; set; } = "";
    public string CertPassword { get; set; } = "";
    /// <summary>C1/C2 路径白名单:为空=允许任意绝对路径(默认,向后兼容);配置后,上传/下载/预检路径强制限定其下。</summary>
    public List<string> AllowedBasePaths { get; set; } = new();

    public static ServerConfig CreateNew(int port, string token, string certPath, string certPassword) =>
        new() { Port = port, Token = token, CertPath = certPath, CertPassword = certPassword };

    /// <summary>L12:null 安全解析;失败抛清晰异常(不被 ! 吞掉)。</summary>
    public static ServerConfig Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path));
        if (cfg is null) throw new InvalidOperationException($"无法解析配置: {path}");
        return cfg;
    }

    /// <summary>C3:校验 token 非空且达最小熵长。不合法直接抛(启动失败优于无认证运行)。</summary>
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Token) || Token.Length < MinTokenLen)
            throw new InvalidOperationException(
                $"Token 无效(为空或少于 {MinTokenLen} 字符)。请删除 server.json 重新初始化。");
    }

    /// <summary>H6 原子写(临时文件 + Move);C4 Windows 上收紧 ACL 仅当前用户/管理员/SYSTEM。</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        RestrictAccess(tmp);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
        RestrictAccess(path);
    }

    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void RestrictAccess(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var info = new FileInfo(path);
            var sec = info.GetAccessControl();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var identity in new[] { WindowsIdentity.GetCurrent().Name, "Administrators", "SYSTEM" })
                sec.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(sec);
        }
        catch { /* 无权限或非 Windows:降级为默认权限 */ }
    }
}
