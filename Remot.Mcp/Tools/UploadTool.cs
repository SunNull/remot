using System.ComponentModel;
using ModelContextProtocol.Server;
using Remot.Client;

namespace Remot.Mcp.Tools;

[McpServerToolType]
public sealed class UploadTool
{
    private readonly RemotClient _client;
    public UploadTool(RemotClient client) => _client = client;

    [McpServerTool, Description("把本地文件上传到远程目标(src/dst 成对)")]
    public async Task<string> remot_upload(
        [Description("目标名")] string target,
        [Description("源文件路径数组")] string[] files,
        [Description("目的路径数组,与 files 同序")] string[] dests)
    {
        var pairs = new List<(string, string)>();
        for (int i = 0; i < files.Length; i++) pairs.Add((files[i], dests[i]));
        var r = await _client.UploadAsync(target, pairs);
        if (!r.Ok) return $"ERROR: {r.Error}";
        return string.Join("\n", r.Value!.Select(x => $"{x.Dest}: {(x.Ok ? "OK" : x.Error)} ({x.Bytes}B)"));
    }
}
