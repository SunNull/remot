using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Remot.Protocol;
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
                c.EnsureValid();   // C3:空 token 不允许注册服务
                ServiceInstaller.Install(Environment.ProcessPath!, c.Port);
                return 0;
            }
        case "rotate-token":
            {
                // C6 缓解:一键轮换 token(证书不变),作废所有旧客户端;合法客户端重 pair 即可
                var c = ServerConfig.Load(cfgPath);
                c.EnsureValid();
                c.Token = ServerConfig.NewToken();
                c.Save(cfgPath);
                using var cert = X509CertificateLoader.LoadPkcs12(
                    File.ReadAllBytes(c.CertPath), c.CertPassword, X509KeyStorageFlags.Exportable);
                var fp = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
                var ps = PairingPayload.Encode(LocalLanIp() ?? Environment.MachineName, c.Port, c.Token, fp);
                AuditLog.SavePairing(ps);
                Console.WriteLine("Token 已轮换 —— 所有旧客户端需重新 pair。新配对串(已写入 pairing.txt):");
                Console.WriteLine(ps);
                return 0;
            }
        case "uninstall": ServiceInstaller.Uninstall(); return 0;
        case "status": Console.WriteLine($"config: {cfgPath}"); return 0;
    }
}

var cfg = File.Exists(cfgPath) ? ServerConfig.Load(cfgPath) : Bootstrap(cfgPath);
cfg.EnsureValid();   // C3:空 token 直接拒绝启动(优于无认证运行)

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceInstaller.ServiceName);
builder.Services.AddGrpc(options => options.Interceptors.Add<TokenInterceptor>());
builder.Services.AddSingleton<TokenInterceptor>(_ => new TokenInterceptor(cfg.Token, cfg.AllowedClientIPs));
builder.Services.AddSingleton<ICommandRunner, CommandRunner>();
builder.Services.AddSingleton<IProcessFactory, ProcessFactory>();
builder.Services.AddSingleton<Hasher>();
builder.Services.AddSingleton(sp => new FileReceiver(sp.GetRequiredService<Hasher>(), cfg.AllowedBasePaths));   // C1:注入允许基目录
builder.Services.AddSingleton<FileSender>();

builder.WebHost.ConfigureKestrel(k =>
{
    void Configure(ListenOptions lo) { lo.Protocols = HttpProtocols.Http2; lo.UseHttps(LoadCert(cfg)); }
    // H3:按配置绑定;默认全网卡,生产可改内网地址
    if (string.IsNullOrWhiteSpace(cfg.BindAddress) || cfg.BindAddress is "0.0.0.0" or "*")
        k.ListenAnyIP(cfg.Port, Configure);
    else
        k.Listen(IPAddress.Parse(cfg.BindAddress), cfg.Port, Configure);
});

var app = builder.Build();
app.MapGrpcService<RemotServiceImpl>();
app.Run();
return 0;

static ServerConfig Bootstrap(string cfgPath)
{
    // L2:用密码学随机数做 PFX 密码,而非 GUID
    var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var (cert, fingerprint) = CertGenerator.GenerateSelfSigned("remot", password);
    var certFile = Path.Combine(Path.GetDirectoryName(cfgPath)!, "server.pfx");
    File.WriteAllBytes(certFile, cert.Export(X509ContentType.Pfx, password));
    var cfg = ServerConfig.CreateNew(7070, ServerConfig.NewToken(), certFile, password);
    cfg.Save(cfgPath);
    var host = LocalLanIp() ?? Environment.MachineName;
    Console.WriteLine("==== Remot 服务端已初始化 ====");
    Console.WriteLine($"本机探测地址:{host}(若不对,开发机用 `remot pair --host <真实IP> \"配对串\"` 覆盖)");
    Console.WriteLine("把下面这行配对串粘到开发机执行 `remot pair`(注:配对串即凭证,用后妥善保管):");
    var ps = PairingPayload.Encode(host, cfg.Port, cfg.Token, fingerprint);
    Console.WriteLine(ps);
    AuditLog.SavePairing(ps);   // M10:服务态下配对串也落盘,可从 pairing.txt 取回
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
