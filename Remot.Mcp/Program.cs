using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Remot.Client;
using Remot.Mcp.Tools;
using System.Reflection;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(new RemotClient(McpConfigPath()));
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(Assembly.GetExecutingAssembly());
await builder.Build().RunAsync();

static string McpConfigPath() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remot", "targets.json");
