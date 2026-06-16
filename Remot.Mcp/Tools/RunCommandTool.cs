using System.ComponentModel;
using ModelContextProtocol.Server;
using Remot.Client;

namespace Remot.Mcp.Tools;

[McpServerToolType]
public sealed class RunCommandTool
{
    private readonly RemotClient _client;
    public RunCommandTool(RemotClient client) => _client = client;

    [McpServerTool, Description("在远程 Windows 目标上批量执行命令,返回每条的结构化结果")]
    public async Task<string> remot_run(
        [Description("目标名")] string target,
        [Description("命令数组")] string[] commands,
        [Description("shell: powershell/pwsh/cmd")] string shell = "powershell",
        int? timeout_ms = null)
    {
        var r = await _client.RunCommandAsync(target, commands, shell, timeout_ms);
        if (!r.Ok) return $"ERROR: {r.Error}";
        return string.Join("\n", r.Value!.Select(x =>
            $"[{x.Index}] exit={x.ExitCode}{(x.TimedOut ? " TIMEOUT" : "")}\n{x.Stdout}\n--- stderr ---\n{x.Stderr}"));
    }
}
