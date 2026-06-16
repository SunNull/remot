using System.Text;
using System.Text.Json;

namespace Remot.Server;

/// <summary>服务端侧:把 host/port/token/证书指纹打包成一行配对串,首启时打印给用户。</summary>
public static class PairingPayload
{
    public static string Encode(string host, int port, string token, string fingerprint)
    {
        var json = JsonSerializer.Serialize(new { host, port, token, fingerprint });
        return "remot://pair#" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
