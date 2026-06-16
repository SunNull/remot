using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Remot.Server;
using Remot.Server.Config;
using Remot.Server.Execution;
using Remot.Server.Files;
using Remot.Server.Security;
using Remot.Server.Services;
using Remot.Server.Setup;

var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Remot");
Directory.CreateDirectory(dataDir);
var cfgPath = Path.Combine(dataDir, "server.json");

if (args.Length > 0)
{
    switch (args[0].ToLowerInvariant())
    {
        case "install":
            {
                var c = File.Exists(cfgPath) ? ServerConfig.Load(cfgPath) : Bootstrap(cfgPath);
                ServiceInstaller.Install(Environment.ProcessPath!, c.Port);
                return 0;
            }
        case "uninstall": ServiceInstaller.Uninstall(); return 0;
        case "status": Console.WriteLine($"config: {cfgPath}"); return 0;
    }
}

var cfg = File.Exists(cfgPath) ? ServerConfig.Load(cfgPath) : Bootstrap(cfgPath);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceInstaller.ServiceName);
builder.Services.AddGrpc(options => options.Interceptors.Add<TokenInterceptor>());
builder.Services.AddSingleton<TokenInterceptor>(_ => new TokenInterceptor(cfg.Token));
builder.Services.AddSingleton<ICommandRunner, CommandRunner>();
builder.Services.AddSingleton<IProcessFactory, ProcessFactory>();
builder.Services.AddSingleton<Hasher>();
builder.Services.AddSingleton<FileReceiver>();
builder.Services.AddSingleton<FileSender>();

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(cfg.Port, lo =>
    {
        lo.Protocols = HttpProtocols.Http2;
        lo.UseHttps(LoadCert(cfg));
    });
});

var app = builder.Build();
app.MapGrpcService<RemotServiceImpl>();
app.Run();
return 0;

static ServerConfig Bootstrap(string cfgPath)
{
    var password = Guid.NewGuid().ToString("N");
    var (cert, fingerprint) = CertGenerator.GenerateSelfSigned("remot", password);
    var certFile = Path.Combine(Path.GetDirectoryName(cfgPath)!, "server.pfx");
    File.WriteAllBytes(certFile, cert.Export(X509ContentType.Pfx, password));
    var cfg = ServerConfig.CreateNew(7070, ServerConfig.NewToken(), certFile, password);
    cfg.Save(cfgPath);
    var host = LocalLanIp() ?? Environment.MachineName;   // 自动取首选局域网 IP,失败回退机器名
    Console.WriteLine("==== Remot 服务端已初始化 ====");
    Console.WriteLine($"本机探测地址:{host}(若不对,开发机用 `remot pair --host <真实IP> \"配对串\"` 覆盖)");
    Console.WriteLine("把下面这行配对串粘到开发机执行 `remot pair`:");
    Console.WriteLine(PairingPayload.Encode(host, cfg.Port, cfg.Token, fingerprint));
    return cfg;
}

static X509Certificate2 LoadCert(ServerConfig cfg) =>
    X509CertificateLoader.LoadPkcs12(
        File.ReadAllBytes(cfg.CertPath), cfg.CertPassword, X509KeyStorageFlags.Exportable);

static string? LocalLanIp() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(a.Address))
        .Select(a => a.Address.ToString())
        .FirstOrDefault();
