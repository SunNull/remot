using System.Security.Cryptography;
using System.Text.Json;

namespace Remot.Server.Config;

public sealed class ServerConfig
{
    public int Port { get; set; } = 7070;
    public string Token { get; set; } = "";
    public string CertPath { get; set; } = "";
    public string CertPassword { get; set; } = "";

    public static ServerConfig CreateNew(int port, string token, string certPath, string certPassword)
        => new() { Port = port, Token = token, CertPath = certPath, CertPassword = certPassword };

    public static ServerConfig Load(string path) =>
        JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path))!;

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
