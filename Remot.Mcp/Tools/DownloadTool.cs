using System.ComponentModel;
using ModelContextProtocol.Server;
using Remot.Client;

namespace Remot.Mcp.Tools;

[McpServerToolType]
public sealed class DownloadTool
{
    private readonly RemotClient _client;
    public DownloadTool(RemotClient client) => _client = client;

    [McpServerTool, Description("从远程目标下载一个文件到本地")]
    public async Task<string> remot_download(
        [Description("目标名")] string target,
        [Description("远程文件路径")] string remotePath,
        [Description("本地保存路径")] string localPath)
    {
        var r = await _client.DownloadAsync(target, remotePath, localPath);
        return r.Ok ? $"OK → {localPath}" : $"ERROR: {r.Error}";
    }
}
