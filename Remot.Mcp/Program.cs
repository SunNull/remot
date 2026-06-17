using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Remot.Client;
using Remot.Mcp.Tools;

// 解析 --config <path>(或 REMOT_CONFIG 环境变量);默认 ~/.remot/targets.json
var configPath = ResolveConfigPath(args);

// 双击/命令行(非管道)→ 打印配置模板;管道(MCP 客户端驱动)→ 服务
if (!Console.IsInputRedirected)
{
    PrintTemplate(configPath);
    return 0;
}

// ── 服务模式 ──
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new RemotClient(configPath));
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(Assembly.GetExecutingAssembly());
await builder.Build().RunAsync();
return 0;

static string ResolveConfigPath(string[] args)
{
    // 优先:--config <path>
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == "--config")
            return args[i + 1];
    // 其次:环境变量
    var env = Environment.GetEnvironmentVariable("REMOT_CONFIG");
    if (!string.IsNullOrEmpty(env)) return env;
    // 默认
    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remot", "targets.json");
}

static void PrintTemplate(string configPath)
{
    var exe = Environment.ProcessPath!;
    var exeEsc = exe.Replace("\\", "\\\\");
    var cfgEsc = configPath.Replace("\\", "\\\\");

    Console.WriteLine("╔══════════════════════════════════════╗");
    Console.WriteLine("║     Remot MCP 配置模板               ║");
    Console.WriteLine("╚══════════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"当前配置路径:{configPath}");
    Console.WriteLine();

    // 已有目标?
    if (File.Exists(configPath))
    {
        var cfg = Remot.Client.Config.TargetsConfig.Load(configPath);
        if (cfg.Targets.Count > 0)
        {
            Console.WriteLine("已登记目标:");
            foreach (var kv in cfg.Targets) Console.WriteLine($"  {kv.Key} → {kv.Value.Host}:{kv.Value.Port}");
            Console.WriteLine();
        }
    }
    else Console.WriteLine("(尚无目标,agent 可用 remot_pair 配对)\n");

    Console.WriteLine("Claude Code  →  ~/.claude.json:");
    Console.WriteLine();
    Console.WriteLine("{");
    Console.WriteLine("  \"mcpServers\": {");
    Console.WriteLine("    \"remot\": {");
    Console.WriteLine($"      \"command\": \"{exeEsc}\",");
    Console.WriteLine($"      \"args\": [\"--config\", \"{cfgEsc}\"]");
    Console.WriteLine("    }");
    Console.WriteLine("  }");
    Console.WriteLine("}");
    Console.WriteLine();
    Console.WriteLine("自定义配置路径:");
    Console.WriteLine("  方式1:args 里传 --config <路径>");
    Console.WriteLine("  方式2:环境变量 REMOT_CONFIG=<路径>");
    Console.WriteLine();
    Console.WriteLine("可用工具:");
    Console.WriteLine("  remot_pair    (pairing_string, name?)         登记目标");
    Console.WriteLine("  remot_run     (target, commands[], shell?)    远程执行");
    Console.WriteLine("  remot_upload  (target, files[{src,dst}])      上传文件");
    Console.WriteLine("  remot_download(target, remotePath, localPath) 下载文件");
    Console.WriteLine("  remot_open_session (target, shell?, cwd?)         打开持久会话(可选,省启动开销)");
    Console.WriteLine("  remot_run_in_session(target, session_id, command)  会话内执行(保持 cwd/env)");
    Console.WriteLine("  remot_close_session(target, session_id)          关闭会话");
    Console.WriteLine();
    Console.WriteLine("按任意键退出...");
    Console.ReadKey(true);
}
