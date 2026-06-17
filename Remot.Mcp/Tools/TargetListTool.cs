using System.ComponentModel;
using ModelContextProtocol.Server;
using Remot.Client;

namespace Remot.Mcp.Tools;

[McpServerToolType]
public sealed class TargetListTool
{
    private readonly RemotClient _client;
    public TargetListTool(RemotClient client) => _client = client;

    [McpServerTool, Description("列出已配置的远程目标(名称/地址)")]
    public Task<string> remot_list_targets()
    {
        var targets = _client.ListTargets();
        if (targets.Count == 0)
            return Task.FromResult("尚无目标。用 remot_pair(pairing_string) 配对。");
        return Task.FromResult(string.Join("\n", targets.Select(t => $"  {t.Name} → {t.Host}:{t.Port}")));
    }
}
