using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Remot.Client;
using Remot.Mcp.Tools;

// 双击(console stdin)→ 向导;管道(Claude Code 驱动)→ 服务模式
if (!Console.IsInputRedirected)
{
    return Wizard();
}

// ── 服务模式(Claude Code 经 stdio 驱动)──
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new RemotClient(McpConfigPath()));
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(Assembly.GetExecutingAssembly());
await builder.Build().RunAsync();
return 0;

// ── 双击向导:装到稳定位置 → 注册 Claude Code ──
static int Wizard()
{
    Console.WriteLine("╔══════════════════════════════════╗");
    Console.WriteLine("║    Remot MCP 注册向导            ║");
    Console.WriteLine("╚══════════════════════════════════╝");

    // 1:复制到稳定位置
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Remot");
    Directory.CreateDirectory(dir);
    var dst = Path.Combine(dir, "Remot.Mcp.exe");
    File.Copy(Environment.ProcessPath!, dst, overwrite: true);
    Console.WriteLine($"✓ 已安装到 {dst}");

    // 2:注册到 Claude Code (~/.claude.json 的 mcpServers)
    Console.Write("\n注册到 Claude Code? (Y/n): ");
    if (Confirm())
    {
        var cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        try
        {
            JsonNode root = File.Exists(cfgPath)
                ? JsonNode.Parse(File.ReadAllText(cfgPath)) ?? new JsonObject()
                : new JsonObject();

            var mcp = root["mcpServers"] as JsonObject ?? new JsonObject();
            mcp["remot"] = new JsonObject
            {
                ["command"] = dst,
                ["args"] = new JsonArray()
            };
            root["mcpServers"] = mcp;

            File.WriteAllText(cfgPath, root.ToString());
            Console.WriteLine($"✓ 已注册到 {cfgPath}");
            Console.WriteLine("  重启 Claude Code 后自动获得:");
            Console.WriteLine("    remot_run(target, commands[], shell?, cwd?)");
            Console.WriteLine("    remot_upload(target, files[{src,dst}])");
            Console.WriteLine("    remot_download(target, remotePath, localPath)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ 注册失败: {ex.Message}");
            Console.WriteLine($"  请手动在 ~/.claude.json 的 mcpServers 加:");
            Console.WriteLine($"    \"remot\": {{ \"command\": \"{dst}\" }}");
        }
    }

    Console.WriteLine("\n══════════════════════════════════════");
    Console.WriteLine("  ✓ 设置完成!重启 Claude Code 即可用。");
    Console.WriteLine("══════════════════════════════════════");
    PauseExit();
    return 0;
}

static string McpConfigPath() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remot", "targets.json");

static bool Confirm() => (Console.ReadLine()?.Trim().ToLowerInvariant() ?? "y") is "" or "y" or "yes";
static void PauseExit() { Console.WriteLine("\n按任意键退出..."); Console.ReadKey(true); }
