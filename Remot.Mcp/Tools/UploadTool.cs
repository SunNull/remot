using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Remot.Client;

namespace Remot.Mcp.Tools;

[McpServerToolType]
public sealed class UploadTool
{
    private readonly RemotClient _client;
    public UploadTool(RemotClient client) => _client = client;

    [McpServerTool, Description("把本地文件上传到远程目标")]
    public async Task<string> remot_upload(
        [Description("目标名")] string target,
        [Description("文件对象数组,形如 [{\"src\":\"本地路径\",\"dst\":\"远程路径\"}, ...]")] JsonElement files)
    {
        // L11:单对象数组 API(替代两数组);内部解析 src/dst
        var pairs = new List<(string, string)>();
        try
        {
            foreach (var item in files.EnumerateArray())
            {
                var src = item.GetProperty("src").GetString();
                var dst = item.GetProperty("dst").GetString();
                if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
                    return "ERROR: 每个文件对象需含非空 src 与 dst";
                pairs.Add((src, dst));
            }
        }
        catch (Exception ex)
        {
            return $"ERROR: 解析 files 失败({ex.Message});期望 [{{\"src\":\"...\",\"dst\":\"...\"}}]";
        }

        var r = await _client.UploadAsync(target, pairs);
        if (!r.Ok) return $"ERROR: {r.Error}";
        return string.Join("\n", r.Value!.Select(x => $"{x.Dest}: {(x.Ok ? "OK" : x.Error)} ({x.Bytes}B)"));
    }
}
