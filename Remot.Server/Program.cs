using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
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
        case "install": return DoInstall(args.Skip(1).ToArray(), cfgPath);
        case "rotate-token":
            {
                var c = ServerConfig.Load(cfgPath);
                c.EnsureValid();
                c.Token = ServerConfig.NewToken();
                c.Save(cfgPath);
                using var cert = X509CertificateLoader.LoadPkcs12(
                    File.ReadAllBytes(c.CertPath), c.CertPassword, X509KeyStorageFlags.Exportable);
                var fp = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
                var ps = PairingPayload.Encode(LocalLanIp() ?? Environment.MachineName, c.Port, c.Token, fp);
                AuditLog.SavePairing(ps);
                ClipboardHelper.SetText(ps);
                Console.WriteLine("Token 已轮换 —— 所有旧客户端需重新 pair。新配对串(已复制到剪贴板 + pairing.txt):");
                Console.WriteLine(ps);
                return 0;
            }
        case "uninstall": ServiceInstaller.Uninstall(); return 0;
        case "status": return DoStatus(cfgPath);
    }
}

var cfg = File.Exists(cfgPath) ? ServerConfig.Load(cfgPath) : Bootstrap(cfgPath);
cfg.EnsureValid();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceInstaller.ServiceName);
builder.Services.AddGrpc(options => options.Interceptors.Add<TokenInterceptor>());
builder.Services.AddSingleton<TokenInterceptor>(_ => new TokenInterceptor(cfg.Token, cfg.AllowedClientIPs));
builder.Services.AddSingleton<ICommandRunner, CommandRunner>();
builder.Services.AddSingleton<IProcessFactory, ProcessFactory>();
builder.Services.AddSingleton<Hasher>();
builder.Services.AddSingleton(sp => new FileReceiver(sp.GetRequiredService<Hasher>(), cfg.AllowedBasePaths));
builder.Services.AddSingleton<FileSender>();

builder.WebHost.ConfigureKestrel(k =>
{
    void Configure(ListenOptions lo) { lo.Protocols = HttpProtocols.Http2; lo.UseHttps(LoadCert(cfg)); }
    if (string.IsNullOrWhiteSpace(cfg.BindAddress) || cfg.BindAddress is "0.0.0.0" or "*")
        k.ListenAnyIP(cfg.Port, Configure);
    else
        k.Listen(IPAddress.Parse(cfg.BindAddress), cfg.Port, Configure);
});

var app = builder.Build();
app.MapGrpcService<RemotServiceImpl>();
app.Run();
return 0;

// ── 安装:自提权(单次 UAC)→ Bootstrap → 复制到 ProgramFiles → 注册服务 → 引导 ──
static int DoInstall(string[] extra, string cfgPath)
{
    if (OperatingSystem.IsWindows() && !IsElevated())
    {
        // 非提权:自我提权重启(只弹一次 UAC),完成后原窗口读 pairing.txt 引导
        Console.WriteLine("需要管理员权限,正在提权(UAC)... ");
        var psi = new ProcessStartInfo(Environment.ProcessPath!, new[] { "install" }.Concat(extra).ToArray())
        { Verb = "runas", UseShellExecute = true };
        try
        {
            var p = Process.Start(psi);
            p?.WaitForExit();
        }
        catch { Console.Error.WriteLine("提权被取消。请以管理员身份重试。"); return 1; }
        // 提权窗口已关;从 pairing.txt 引导
        var pf = Path.Combine(Path.GetDirectoryName(cfgPath)!, "pairing.txt");
        if (File.Exists(pf))
        {
            var ps = File.ReadAllText(pf).Trim();
            Console.WriteLine("\n✓ 安装完成(配对串已复制到剪贴板 + 写入 pairing.txt)。");
            Console.WriteLine($"\n下一步 —— 在开发机执行:\n  remot pair \"{ps}\"\n  remot run -t <目标名> \"echo ok\"");
        }
        else Console.WriteLine("\n安装可能未完成,请以管理员身份运行 `Remot.Server.exe install` 查看。");
        return 0;
    }

    // 已提权:复制自身到 ProgramFiles → Bootstrap → 注册 → 写剪贴板
    var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Remot");
    Directory.CreateDirectory(installDir);
    var exePath = Path.Combine(installDir, "Remot.Server.exe");
    File.Copy(Environment.ProcessPath!, exePath, overwrite: true);
    Console.WriteLine($"✓ 已安装到 {exePath}");

    var c = File.Exists(cfgPath) ? ServerConfig.Load(cfgPath) : Bootstrap(cfgPath);
    c.EnsureValid();
    Console.WriteLine("✓ 配置已就绪");
    ServiceInstaller.Install(exePath, c.Port);
    Console.WriteLine("✓ 服务已注册并启动");

    // 配对串写剪贴板 + pairing.txt(Bootstrap 已写 pairing.txt),这里复制到剪贴板
    var pf2 = Path.Combine(Path.GetDirectoryName(cfgPath)!, "pairing.txt");
    if (File.Exists(pf2)) ClipboardHelper.SetText(File.ReadAllText(pf2).Trim());

    // 等服务起来后自检端口
    Thread.Sleep(1500);
    var listening = IsPortListening(c.Port);
    Console.WriteLine(listening ? $"✓ 端口 {c.Port} 已监听" : $"⚠ 端口 {c.Port} 未监听(稍等或检查服务状态)");

    Console.WriteLine("\n安装完成。下一步 —— 开发机:remot pair \"<配对串>\"");
    return 0;
}

// ── status:服务状态 + 端口 + 配置 ──
static int DoStatus(string cfgPath)
{
    Console.WriteLine($"配置:{cfgPath}");
    if (File.Exists(cfgPath))
    {
        var c = ServerConfig.Load(cfgPath);
        Console.WriteLine($"端口:{c.Port}  绑定:{c.BindAddress}  IP白名单:{(c.AllowedClientIPs.Count > 0 ? string.Join(",", c.AllowedClientIPs) : "不限")}");
        var pf = Path.Combine(Path.GetDirectoryName(cfgPath)!, "pairing.txt");
        if (File.Exists(pf)) Console.WriteLine($"配对串:见 {pf}");
        var audit = Path.Combine(Path.GetDirectoryName(cfgPath)!, "audit.log");
        if (File.Exists(audit)) Console.WriteLine($"审计:{audit}({new FileInfo(audit).Length} 字节)");
    }
    else Console.WriteLine("尚未初始化(先运行 install)。");
    return 0;
}

static bool IsElevated()
{
    if (!OperatingSystem.IsWindows()) return false;
    try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
    catch { return false; }
}

static bool IsPortListening(int port)
{
    try
    {
        var props = IPGlobalProperties.GetIPGlobalProperties();
        return props.GetActiveTcpListeners().Any(ep => ep.Port == port);
    }
    catch { return false; }
}

static ServerConfig Bootstrap(string cfgPath)
{
    var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var (cert, fingerprint) = CertGenerator.GenerateSelfSigned("remot", password);
    var certFile = Path.Combine(Path.GetDirectoryName(cfgPath)!, "server.pfx");
    File.WriteAllBytes(certFile, cert.Export(X509ContentType.Pfx, password));
    var cfg = ServerConfig.CreateNew(7070, ServerConfig.NewToken(), certFile, password);
    cfg.Save(cfgPath);
    var host = LocalLanIp() ?? Environment.MachineName;
    var ps = PairingPayload.Encode(host, cfg.Port, cfg.Token, fingerprint);
    AuditLog.SavePairing(ps);   // 落盘(剪贴板在 DoInstall 里复制)
    Console.WriteLine($"✓ 自签证书已生成(指纹 {fingerprint[..16]}...)");
    Console.WriteLine($"✓ 配对串: {ps}");
    Console.WriteLine($"  (探测地址 {host};若不对,开发机用 remot pair --host <真实IP> 覆盖)");
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
