using System.Text;
using System.Text.Json;

namespace Remot.Client.Pairing;

public static class PairingString
{
    private const string Prefix = "remot://pair#";

    public static string Encode(string host, int port, string token, string fingerprint)
    {
        var json = JsonSerializer.Serialize(new { host, port, token, fingerprint });
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static (string Host, int Port, string Token, string Fingerprint) Decode(string s)
    {
        if (!s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("不是合法的 remot 配对串");
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(s[Prefix.Length..]));
        var doc = JsonDocument.Parse(json).RootElement;
        return (doc.GetProperty("host").GetString()!,
                doc.GetProperty("port").GetInt32(),
                doc.GetProperty("token").GetString()!,
                doc.GetProperty("fingerprint").GetString()!);
    }
}
