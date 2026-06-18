using System.ComponentModel;
using ModelContextProtocol.Server;
using Remot.Client;

namespace Remot.Mcp.Tools;

/// <summary>优化3:持久会话工具。适合连续多条命令、需保持工作目录/环境的场景。
/// 普通单次命令仍用 remot_run;会话用完务必 remot_close_session 释放进程。</summary>
[McpServerToolType]
public sealed class SessionTool
{
    private readonly RemotClient _client;
    public SessionTool(RemotClient client) => _client = client;

    [McpServerTool, Description("打开持久 shell 会话(跨命令保持 cwd/env,省 shell 启动开销)。返回 session_id")]
    public async Task<string> remot_open_session(
        [Description("目标名")] string target,
        [Description("shell: powershell/pwsh/cmd(留空=自动)")] string? shell = null,
        [Description("工作目录,可选")] string? cwd = null)
    {
        var r = await _client.OpenSessionAsync(target, shell, cwd);
        return r.Ok ? $"✓ session 已打开:{r.Value}\n用 remot_run_in_session 执行命令,完成后 remot_close_session 关闭。" : $"ERROR: {r.Error}";
    }

    [McpServerTool, Description("在持久会话里执行一条命令(保持之前的 cwd/env)。默认超时 30 秒")]
    public async Task<string> remot_run_in_session(
        [Description("目标名")] string target,
        [Description("OpenSession 返回的 session_id")] string session_id,
        [Description("命令")] string command,
        [Description("超时毫秒,可选(默认 30000)")] int? timeout_ms = null)
    {
        var r = await _client.RunInSessionAsync(target, session_id, command, timeout_ms ?? 30000);
        if (!r.Ok) return $"ERROR: {r.Error}";
        return string.Join("\n", r.Value!.Select(x =>
            $"exit={x.ExitCode}{(x.TimedOut ? " TIMEOUT" : "")}\n{x.Stdout}\n--- stderr ---\n{x.Stderr}"));
    }

    [McpServerTool, Description("关闭持久会话(释放 shell 进程)")]
    public async Task<string> remot_close_session(
        [Description("目标名")] string target,
        [Description("session_id")] string session_id)
    {
        var r = await _client.CloseSessionAsync(target, session_id);
        return r.Ok ? $"✓ session 已关闭:{session_id[..Math.Min(8, session_id.Length)]}" : $"ERROR: {r.Error}";
    }
}
