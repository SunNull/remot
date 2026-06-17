using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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

// ── 双击(无参+交互)→ 安装向导;无参+非交互(Windows 服务)→ 服务器模式 ──
if (args.Length == 0)
{
    if (!Environment.UserInteractive) goto RunServer;   // 被 SCM 启动(服务)→ 直接跑

    var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Remot");
    Directory.CreateDirectory(dataDir);
    var cfgPath = Path.Combine(dataDir, "server.json");
    bool alreadyInstalled = File.Exists(cfgPath);

    Console.WriteLine("╔══════════════════════════════════╗");
    Console.WriteLine("║      Remot 服务端安装向导        ║");
    Console.WriteLine("╚══════════════════════════════════╝");

    if (alreadyInstalled)
    {
        // 已安装:提示是否重置
        Console.WriteLine("\n检测到已有配置(保留原有证书和配对串)。");
        Console.Write("重置安装(重新生成证书+token)? (y/N): ");
        if (!ConfirmDefaultNo())   // 默认 N = 只更新 exe + 重启服务
        {
            return DoInstall(new[] { "--keep-config" }, cfgPath, interactive: true);
        }
        // 选择 Y = 删旧配置,按首次安装走
        try { File.Delete(cfgPath); } catch { }
    }

    Console.Write("\n继续安装? (Y/n): ");
    if (!Confirm()) { Console.WriteLine("已取消。"); PauseExit(); return 0; }
    Console.Write("服务器名称(可选,回车=机器名): ");
    var nameInput = Console.ReadLine()?.Trim();
    Console.Write("允许文件操作的根目录(逗号分隔,留空=不限制。⚠ 留空=认证后可读写服务账户可达的任意路径): ");
    var rootsInput = Console.ReadLine()?.Trim();
    var installArgs = new List<string>();
    if (!string.IsNullOrEmpty(nameInput)) installArgs.AddRange(new[] { "--name", nameInput });
    if (!string.IsNullOrEmpty(rootsInput)) installArgs.AddRange(new[] { "--roots", rootsInput });
    return DoInstall(installArgs.ToArray(), cfgPath, interactive: true);
}
// 命令行模式
{
    var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Remot");
    Directory.CreateDirectory(dataDir);
    var cfgPath = Path.Combine(dataDir, "server.json");
    switch (args[0].ToLowerInvariant())
    {
        case "install": return DoInstall(args.Skip(1).ToArray(), cfgPath, interactive: false);
        case "run": goto RunServer;   // 调试:控制台模式运行
        case "rotate-token": {
                var c = ServerConfig.Load(cfgPath);
                c.EnsureValid();
                c.Token = ServerConfig.NewToken();
                c.Save(cfgPath);
                using var cert = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(c.CertPath), c.CertPassword);   // L2:服务端无需导出私钥
                var fp = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
                var ps = PairingPayload.Encode(LocalLanIp() ?? Environment.MachineName, c.Port, c.Token, fp, c.Name);
                AuditLog.SavePairing(ps); ClipboardHelper.SetText(ps);
                Console.WriteLine("Token 已轮换 —— 新配对串(剪贴板 + pairing.txt):");
                Console.WriteLine(ps); return 0;
        }
        case "uninstall": ServiceInstaller.Uninstall(); return 0;
        case "status": return DoStatus(cfgPath);
    }
}

RunServer:;
var _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Remot");
Directory.CreateDirectory(_dataDir);
var _cfgPath = Path.Combine(_dataDir, "server.json");
ServerConfig cfg;
try { cfg = ServerConfig.Load(_cfgPath); }
catch (Exception ex)   // L12:配置缺失/损坏给友好提示,而非裸异常崩溃
{
    Console.Error.WriteLine($"加载配置失败:{ex.Message}");
    AuditLog.Log($"启动失败:加载配置 - {ex.Message}");
    return 1;
}
try { cfg.EnsureValid(); }
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    AuditLog.Log($"启动失败:配置校验 - {ex.Message}");
    return 1;
}
{
    var builder = WebApplication.CreateBuilder();
    builder.Services.AddWindowsService(o => o.ServiceName = ServiceInstaller.ServiceName);
    builder.Services.AddGrpc(options => options.Interceptors.Add<TokenInterceptor>());
    builder.Services.AddSingleton<TokenInterceptor>(_ => new TokenInterceptor(cfg.Token, cfg.AllowedClientIPs));
    builder.Services.AddSingleton<ICommandRunner, CommandRunner>();
    builder.Services.AddSingleton<IProcessFactory, ProcessFactory>();
    builder.Services.AddSingleton(cfg);   // 给 RemotServiceImpl 注入 ServerConfig(CommandGuard 用)
    builder.Services.AddSingleton<Hasher>();
    builder.Services.AddSingleton(sp => new FileReceiver(sp.GetRequiredService<Hasher>(), cfg.AllowedBasePaths));
    builder.Services.AddSingleton(sp => new FileSender(sp.GetRequiredService<Hasher>(), cfg.AllowedBasePaths));
    builder.WebHost.ConfigureKestrel(k =>
    {
        void Cfg(ListenOptions lo) { lo.Protocols = HttpProtocols.Http2; lo.UseHttps(LoadCert(cfg)); }
        if (string.IsNullOrWhiteSpace(cfg.BindAddress) || cfg.BindAddress is "0.0.0.0" or "*")
            k.ListenAnyIP(cfg.Port, Cfg);
        else
            k.Listen(IPAddress.Parse(cfg.BindAddress), cfg.Port, Cfg);
    });
    var app = builder.Build();
    app.MapGrpcService<RemotServiceImpl>();
    app.Run();
    return 0;
}

// ── 安装:自提权 → 向导 → 全自动 ──
static int DoInstall(string[] extra, string cfgPath, bool interactive)
{
    if (OperatingSystem.IsWindows() && !IsElevated())
    {
        Console.WriteLine("需要管理员权限,正在提权(UAC,仅 1 次)...");
        var psi = new ProcessStartInfo(Environment.ProcessPath!, new[] { "install" }.Concat(extra).ToArray())
        { Verb = "runas", UseShellExecute = true };
        try { var p = Process.Start(psi); p?.WaitForExit(); }
        catch { Console.Error.WriteLine("提权被取消。"); if (interactive) PauseExit(); return 1; }
        var pf = Path.Combine(Path.GetDirectoryName(cfgPath)!, "pairing.txt");
        if (File.Exists(pf))
        {
            Console.WriteLine("\n══════════════════════════════════════");
            Console.WriteLine("  ✓ 安装完成!");
            Console.WriteLine("  配对串已复制到剪贴板,直接在开发机 Ctrl+V。");
            Console.WriteLine("  开发机:双击 Remot.Mcp.exe 获取 MCP 配置模板,粘贴到 Claude Code");
            Console.WriteLine("══════════════════════════════════════");
        }
        else Console.WriteLine("\n安装可能未完成,请以管理员运行 install。");
        if (interactive) PauseExit();
        return 0;
    }

    // 已提权:解析参数
    bool keepConfig = extra.Contains("--keep-config");
    string serverName = "";
    string? roots = null;
    for (int i = 0; i < extra.Length - 1; i++)
    {
        if (extra[i] == "--name") serverName = extra[i + 1];
        else if (extra[i] == "--roots") roots = extra[i + 1];
    }

    try
    {
    Console.WriteLine("\n▶ 停止旧服务 ...");
    try { var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", $"stop {ServiceInstaller.ServiceName}") { UseShellExecute = false, CreateNoWindow = true }; System.Diagnostics.Process.Start(psi)?.WaitForExit(); } catch { }
    Thread.Sleep(1000);

    Console.WriteLine("▶ 安装到 C:\\Program Files\\Remot ...");
    var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Remot");
    Directory.CreateDirectory(installDir);
    var exePath = Path.Combine(installDir, "Remot.Server.exe");
    File.Copy(Environment.ProcessPath!, exePath, overwrite: true);
    Console.WriteLine("  ✓ 已安装");

    ServerConfig c;
    if (keepConfig && File.Exists(cfgPath))
    {
        c = ServerConfig.Load(cfgPath);
        Console.WriteLine("  ✓ 保留原有配置(证书/token/配对串不变)");
    }
    else
    {
        Console.WriteLine("▶ 生成证书 + 配置 ...");
        c = Bootstrap(cfgPath, serverName, roots);
    }
    c.EnsureValid();
    Console.WriteLine("  ✓ 配置就绪");

    Console.WriteLine("▶ 注册 Windows 服务 + 防火墙 ...");
    ServiceInstaller.Install(exePath, c.Port);
    Console.WriteLine("  ✓ 服务已注册并启动");

    Console.WriteLine("▶ 配对串写入剪贴板 ...");
    var pf2 = Path.Combine(Path.GetDirectoryName(cfgPath)!, "pairing.txt");
    if (File.Exists(pf2)) ClipboardHelper.SetText(File.ReadAllText(pf2).Trim());
    Console.WriteLine("  ✓ 已复制到剪贴板");

    Thread.Sleep(1500);
    var listening = IsPortListening(c.Port);
    Console.WriteLine(listening ? $"▶ 端口 {c.Port} ✓ 已监听" : $"▶ 端口 {c.Port} ⚠ 未监听(稍等)");

    Console.WriteLine("\n══════════════════════════════════════");
    Console.WriteLine("  ✓ 全部完成!配对串已在剪贴板。");
    Console.WriteLine("══════════════════════════════════════");
    }
    catch (Exception ex)
    {
        var msg = $"[{DateTime.Now:O}] 安装失败:\n{ex}\n";
        Console.Error.WriteLine($"\n✗ 安装失败:{ex.Message}");
        Console.Error.WriteLine($"  详细已写入:{Path.Combine(Path.GetDirectoryName(cfgPath)!, "install-error.log")}");
        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(cfgPath)!, "install-error.log"), msg); } catch { }
    }
    if (interactive) PauseExit();
    return 0;
}

static int DoStatus(string cfgPath)
{
    Console.WriteLine($"配置: {cfgPath}");
    if (File.Exists(cfgPath))
    {
        var c = ServerConfig.Load(cfgPath);
        Console.WriteLine($"端口:{c.Port}  绑定:{c.BindAddress}  IP白名单:{(c.AllowedClientIPs.Count > 0 ? string.Join(",", c.AllowedClientIPs) : "不限")}");
        var pf = Path.Combine(Path.GetDirectoryName(cfgPath)!, "pairing.txt");
        if (File.Exists(pf)) Console.WriteLine($"配对串:见 {pf}");
    }
    else Console.WriteLine("尚未初始化(双击 exe 安装)。");
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
    try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == port); }
    catch { return false; }
}

static bool Confirm() => (Console.ReadLine()?.Trim().ToLowerInvariant() ?? "y") is "" or "y" or "yes";
static bool ConfirmDefaultNo() => (Console.ReadLine()?.Trim().ToLowerInvariant() ?? "n") is "y" or "yes";
static void PauseExit() { Console.WriteLine("\n按任意键退出..."); Console.ReadKey(true); }

static ServerConfig Bootstrap(string cfgPath, string name = "", string? roots = null)
{
    var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var (cert, fingerprint) = CertGenerator.GenerateSelfSigned("remot", password);
    var certFile = Path.Combine(Path.GetDirectoryName(cfgPath)!, "server.pfx");
    File.WriteAllBytes(certFile, cert.Export(X509ContentType.Pfx, password));
    // S1:把向导收集的根目录写入配置(留空=不限制,EnsureValid 会记审计警告)
    var allowed = string.IsNullOrWhiteSpace(roots)
        ? new List<string>()
        : roots.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    var cfg = ServerConfig.CreateNew(7070, ServerConfig.NewToken(), certFile, password, allowed);
    cfg.Name = string.IsNullOrEmpty(name) ? Environment.MachineName : name;
    cfg.Save(cfgPath);
    var host = LocalLanIp() ?? Environment.MachineName;
    var ps = PairingPayload.Encode(host, cfg.Port, cfg.Token, fingerprint, cfg.Name);
    AuditLog.SavePairing(ps);
    return cfg;
}

static X509Certificate2 LoadCert(ServerConfig cfg) =>
    X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(cfg.CertPath), cfg.CertPassword);   // L2:服务端无需导出私钥

static string? LocalLanIp() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
        .Select(a => a.Address.ToString()).FirstOrDefault();
