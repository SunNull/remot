using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Remot.Client;
using Remot.Mcp.Tools;

// 双击/命令行(非管道)→ 打印配置模板;管道(MCP 客户端驱动)→ 服务
if (!Console.IsInputRedirected)
{
    PrintTemplate();
    return 0;
}

// ── 服务模式 ──
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new RemotClient(McpConfigPath()));
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(Assembly.GetExecutingAssembly());
await builder.Build().RunAsync();
return 0;

static void PrintTemplate()
{
    var exe = Environment.ProcessPath!.Replace("\\", "\\\\");
    Console.WriteLine("╔══════════════════════════════════════╗");
    Console.WriteLine("║     Remot MCP 配置模板               ║");
    Console.WriteLine("╚══════════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine("Claude Code  →  ~/.claude.json:");
    Console.WriteLine();
    Console.WriteLine("{");
    Console.WriteLine("  \"mcpServers\": {");
    Console.WriteLine("    \"remot\": {");
    Console.WriteLine($"      \"command\": \"{exe}\"");
    Console.WriteLine("    }");
    Console.WriteLine("  }");
    Console.WriteLine("}");
    Console.WriteLine();
    Console.WriteLine("Cursor / 其他 MCP 客户端:");
    Console.WriteLine($"  stdio 指向同一路径:{Environment.ProcessPath}");
    Console.WriteLine();
    Console.WriteLine("可用工具:");
    Console.WriteLine("  remot_run     (target, commands[], shell?, cwd?)");
    Console.WriteLine("  remot_upload  (target, files[{src,dst}])");
    Console.WriteLine("  remot_download(target, remotePath, localPath)");
    Console.WriteLine();
    Console.WriteLine("前提:目标需先用 Remot.Cli.exe 双击向导配对。");
    Console.WriteLine();
    Console.WriteLine("按任意键退出...");
    Console.ReadKey(true);
}

static string McpConfigPath() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remot", "targets.json");
