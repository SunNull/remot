using System.Text;
using System.Text.Json;

namespace Remot.Client.Pairing;

public static class PairingString
{
    private const string Prefix = "remot://pair#";

    public static string Encode(string host, int port, string token, string fingerprint, string name = "")
    {
        var json = JsonSerializer.Serialize(new { name, host, port, token, fingerprint });
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static (string Name, string Host, int Port, string Token, string Fingerprint) Decode(string s)
    {
        if (!s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("不是合法的 remot 配对串");
        string json;
        try { json = Encoding.UTF8.GetString(Convert.FromBase64String(s[Prefix.Length..])); }
        catch { throw new FormatException("配对串 base64 解码失败"); }

        var doc = JsonDocument.Parse(json).RootElement;
        var name = doc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var host = GetRequired(doc, "host");          // M9:校验非空
        var token = GetRequired(doc, "token");
        var fingerprint = GetRequired(doc, "fingerprint");
        if (!doc.TryGetProperty("port", out var pe) || pe.ValueKind != JsonValueKind.Number)
            throw new FormatException("配对串缺少 port");
        return (name, host, pe.GetInt32(), token, fingerprint);
    }

    private static string GetRequired(JsonElement doc, string key)
    {
        if (!doc.TryGetProperty(key, out var v) || string.IsNullOrWhiteSpace(v.GetString()))
            throw new FormatException($"配对串缺少或为空:{key}");
        return v.GetString()!;
    }
}
