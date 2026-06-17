using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Remot.Server.Config;

public sealed class ServerConfig
{
    private const int MinTokenLen = 32;

    public int Port { get; set; } = 7070;
    public string Name { get; set; } = "";   // 服务器标识(配对串携带,客户端可识别)
    public string BindAddress { get; set; } = "0.0.0.0";
    public string Token { get; set; } = "";
    public string CertPath { get; set; } = "";
    public string CertPassword { get; set; } = "";
    public List<string> AllowedBasePaths { get; set; } = new();
    /// <summary>命令黑名单(正则,空=不拦截任何命令)。</summary>
    public List<string> BlockedCommands { get; set; } = new()
    {
        // 默认配置(可删可改)
        @"\bshutdown\b", @"\brestart-computer\b", @"\bstop-computer\b",
        @"\bformat\b\s+[a-z]:",
        @"\breg\s+delete\b", @"remove-item.*HKLM:\\", @"remove-item.*HKCU:\\",
        @"\bnet\s+user\b.*\S+\s+\S", @"\bnet\s+localgroup\b",
        @"\bsc\s+delete\b", @"\bsc\s+create\b", @"\bremove-service\b",
        @"\bdel\s+/[sSq].*\\\*", @"\brmdir\s+/[sS].*\\\*", @"remove-item\s+.*-recurse.*\\\*",
        @"\biex\b.*\birm\b", @"\biex\b.*\binvoke-webrequest\b",
        @"\bcurl\b.*\|\s*(bash|sh|pwsh|powershell)",
        @"\bschtasks\s+/create\b", @"register-scheduledtask\b",
        @"\bregsvr32\b", @"\bdiskpart\b", @"\bbcdedit\b",
    };
    /// <summary>受保护的服务名(禁止 stop/delete)。</summary>
    public List<string> ProtectedServices { get; set; } = new() { "RemotServer" };
    /// <summary>受保护的路径(禁止删除)。</summary>
    public List<string> ProtectedPaths { get; set; } = new()
    {
        @"C:\Windows\System32", @"C:\Windows\SysWOW64",
        @"C:\Program Files", @"C:\Program Files (x86)",
        @"C:\ProgramData\Remot",
    };
    /// <summary>C6 缓解:为空=不限;配置后仅这些 IP 的客户端可用 token。</summary>
    public List<string> AllowedClientIPs { get; set; } = new();

    public static ServerConfig CreateNew(int port, string token, string certPath, string certPassword,
        IEnumerable<string>? allowedBasePaths = null)
    {
        var c = new ServerConfig { Port = port, Token = token, CertPath = certPath, CertPassword = certPassword };
        if (allowedBasePaths is not null) c.AllowedBasePaths = allowedBasePaths.ToList();
        return c;
    }

    public static ServerConfig Load(string path)
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path));
            if (cfg is null) throw new InvalidOperationException($"无法解析配置: {path}");
            return cfg;
        }
        catch (JsonException ex)   // M8:损坏 json 给出友好提示而非裸 JsonException
        {
            throw new InvalidOperationException($"配置文件损坏({path}),请删除后重新初始化。", ex);
        }
    }

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Token) || Token.Length < MinTokenLen)
            throw new InvalidOperationException(
                $"Token 无效(为空或少于 {MinTokenLen} 字符)。请删除 server.json 重新初始化。");
        if (Port < 1 || Port > 65535)   // M4
            throw new InvalidOperationException($"Port 无效:{Port}(需 1-65535)。");
        // M4:BindAddress 可解析(0.0.0.0/* 表示全部)
        if (!string.IsNullOrWhiteSpace(BindAddress) && BindAddress is not "0.0.0.0" and not "*")
        {
            try { IPAddress.Parse(BindAddress); }
            catch (Exception ex) { throw new InvalidOperationException($"BindAddress 无法解析:{BindAddress}", ex); }
        }
        if (!File.Exists(CertPath))   // M4
            throw new InvalidOperationException($"证书文件不存在:{CertPath}。请删除 server.json 重新初始化。");

        // S1:AllowedBasePaths 为空 = 认证后可读写全盘。不阻止启动(保持兼容),但记审计警告。
        if (AllowedBasePaths.Count == 0)
            AuditLog.Log("⚠ 安全提示:AllowedBasePaths 为空 —— 认证后客户端可读写服务账户可达的任意路径。建议在 server.json 配置 AllowedBasePaths 限定根目录。");
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        FileProtection.Restrict(tmp);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
        FileProtection.Restrict(path);
    }

    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
