using System.ComponentModel;
using ModelContextProtocol.Server;
using Remot.Client;
using Remot.Client.Config;
using Remot.Client.Pairing;

namespace Remot.Mcp.Tools;

[McpServerToolType]
public sealed class PairTool
{
    private readonly RemotClient _client;
    public PairTool(RemotClient client) => _client = client;

    [McpServerTool, Description("用配对串登记一个远程目标(agent 可自行配对)")]
    public Task<string> remot_pair(
        [Description("配对串 remot://pair#...")] string pairing_string,
        [Description("目标名(可选,默认用 host)")] string? name = null)
    {
        try
        {
            var p = PairingString.Decode(pairing_string);
            var targetName = name ?? (string.IsNullOrEmpty(p.Name) ? $"target-{p.Host}" : p.Name);
            var t = new Target(targetName, p.Host, p.Port, p.Token, p.Fingerprint);
            _client.SaveTarget(t);
            return Task.FromResult($"✓ 已登记 {t.Name} → {t.Host}:{t.Port}。可用 remot_run / remot_upload / remot_download。");
        }
        catch (Exception ex) { return Task.FromResult($"ERROR: {ex.Message}"); }
    }
}
