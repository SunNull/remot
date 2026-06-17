using System.Text;
using System.Text.Json;

namespace Remot.Server;

/// <summary>服务端侧:把 name/host/port/token/证书指纹打包成一行配对串。</summary>
public static class PairingPayload
{
    public static string Encode(string host, int port, string token, string fingerprint, string name = "")
    {
        var json = JsonSerializer.Serialize(new { name, host, port, token, fingerprint });
        return "remot://pair#" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
