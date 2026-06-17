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
    /// <summary>C6 缓解:为空=不限;配置后仅这些 IP 的客户端可用 token。</summary>
    public List<string> AllowedClientIPs { get; set; } = new();

    public static ServerConfig CreateNew(int port, string token, string certPath, string certPassword) =>
        new() { Port = port, Token = token, CertPath = certPath, CertPassword = certPassword };

    public static ServerConfig Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path));
        if (cfg is null) throw new InvalidOperationException($"无法解析配置: {path}");
        return cfg;
    }

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Token) || Token.Length < MinTokenLen)
            throw new InvalidOperationException(
                $"Token 无效(为空或少于 {MinTokenLen} 字符)。请删除 server.json 重新初始化。");
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
