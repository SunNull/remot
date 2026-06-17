using Remot.Client;
using Remot.Client.Config;
using Remot.Client.Pairing;
using Remot.Protocol;

// 双击(无参)→ 向导;有参数 → 命令行模式
if (args.Length == 0)
{
    return Wizard();
}

// 命令行模式
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--deadline" && i + 1 < args.Length && int.TryParse(args[i + 1], out var sec) && sec > 0)
        cts.CancelAfter(TimeSpan.FromSeconds(sec));
return await RunSafely(args, cts.Token);

// ── 双击向导 ──
static int Wizard()
{
    Console.WriteLine("╔══════════════════════════════════╗");
    Console.WriteLine("║      Remot 客户端设置向导        ║");
    Console.WriteLine("╚══════════════════════════════════╝");

    // 步骤 1:装 PATH
    Console.Write("\n1. 安装 remot 命令到 PATH(之后任意目录可用)? (Y/n): ");
    if (Confirm())
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Remot");
        Directory.CreateDirectory(dir);
        File.Copy(Environment.ProcessPath!, Path.Combine(dir, "remot.exe"), overwrite: true);
        var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        if (!path.Contains(dir, StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", string.IsNullOrEmpty(path) ? dir : path + ";" + dir, EnvironmentVariableTarget.User);
        Console.WriteLine("  ✓ 已安装 remot 到 PATH(新终端生效)");
    }
    else Console.WriteLine("  跳过。");

    // 步骤 2:配对目标
    Console.Write("\n2. 配对到服务端? (Y/n): ");
    if (Confirm())
    {
        var clip = ClipboardHelper.GetText();
        string? pairing = null;
        if (clip is not null && clip.StartsWith("remot://pair#", StringComparison.OrdinalIgnoreCase))
        { pairing = clip; Console.WriteLine("  (从剪贴板读取配对串)"); }
        else
        {
            Console.Write("  请粘贴配对串(remot://pair#...): ");
            pairing = Console.ReadLine()?.Trim();
        }
        if (pairing is not null && pairing.StartsWith("remot://pair#"))
        {
            var cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remot", "targets.json");
            try
            {
                var p = PairingString.Decode(pairing);
                var t = new Target($"target-{p.Host}", p.Host, p.Port, p.Token, p.Fingerprint);
                using var c = new RemotClient(cfgPath); c.SaveTarget(t);
                Console.WriteLine($"  ✓ 已登记 → {t.Host}:{t.Port}");
                // 连通自检
                try
                {
                    var stub = new RemotService.RemotServiceClient(c.GetChannel(t));
                    stub.CheckFileAsync(new FileCheckRequest { DestPath = "__ping__", Size = 0, Sha256 = "" },
                        headers: new() { { "authorization", $"Bearer {p.Token}" } }).GetAwaiter().GetResult();
                    Console.WriteLine("  ✓ 连通正常,token 有效");
                }
                catch (Exception ex) { Console.WriteLine($"  ⚠ 连通自检失败:{ex.Message}"); }
            }
            catch (Exception ex) { Console.WriteLine($"  ⚠ 配对失败:{ex.Message}"); }
        }
        else Console.WriteLine("  配对串无效,跳过。");
    }
    else Console.WriteLine("  跳过。");

    // 步骤 3:试运行
    Console.Write("\n3. 试运行一条命令? (Y/n): ");
    if (Confirm())
    {
        Console.Write("  目标名(回车=第一个): ");
        var name = Console.ReadLine()?.Trim();
        Console.Write("  命令(回车=echo ok): ");
        var cmd = Console.ReadLine()?.Trim(); if (string.IsNullOrEmpty(cmd)) cmd = "echo ok";
        var cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remot", "targets.json");
        using var c = new RemotClient(cfgPath);
        var names = c.TargetNames;
        if (names.Count == 0) { Console.WriteLine("  无已登记目标,跳过。"); }
        else
        {
            var target = string.IsNullOrEmpty(name) ? names[0] : name;
            var r = c.RunCommandAsync(target, new[] { cmd }, shell: "cmd").GetAwaiter().GetResult();
            if (r.Ok) foreach (var x in r.Value!) Console.WriteLine($"  [{x.Index}] exit={x.ExitCode}\n  {x.Stdout}");
            else Console.WriteLine($"  错误: {r.Error}");
        }
    }

    Console.WriteLine("\n══════════════════════════════════════");
    Console.WriteLine("  ✓ 设置完成!");
    Console.WriteLine("  之后可用:remot run -t <目标> \"命令\"");
    Console.WriteLine("           remot upload -t <目标> ...");
    Console.WriteLine("══════════════════════════════════════");
    PauseExit();
    return 0;
}

// ── 命令行模式 ──
static async Task<int> RunSafely(string[] args, CancellationToken ct)
{
    try { return await Run(args, ct); }
    catch (OperationCanceledException) { Console.Error.WriteLine("已取消"); return 130; }
    catch (Exception ex) { Console.Error.WriteLine($"错误: {ex.Message}"); return 1; }
}

static async Task<int> Run(string[] args, CancellationToken ct)
{
    string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remot", "targets.json");
    var cmd = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--config" && i + 1 < args.Length) { configPath = args[++i]; continue; }
        if (args[i] == "--deadline") { if (i + 1 < args.Length) i++; continue; }
        cmd.Add(args[i]);
    }
    if (cmd.Count == 0) return PrintHelp();

    return cmd[0].ToLowerInvariant() switch
    {
        "pair" => await Pair(configPath, cmd.Skip(1).ToArray()),
        "run" => await RunCmd(configPath, cmd.Skip(1).ToArray(), ct),
        "upload" => await Upload(configPath, cmd.Skip(1).ToArray(), ct),
        "download" => await Download(configPath, cmd.Skip(1).ToArray(), ct),
        "ping" => await PingCmd(configPath, cmd.Skip(1).ToArray(), ct),
        "install-cli" => InstallCli(),
        "target" => TargetCmd(configPath, cmd.Skip(1).ToArray()),
        "help" or "-h" or "--help" => PrintHelp(),
        _ => Unknown(cmd[0])
    };
}

static async Task<int> Pair(string cfg, string[] a)
{
    string? name = null, host = null, pairing = null;
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i] == "--name" && i + 1 < a.Length) name = a[++i];
        else if (a[i] == "--host" && i + 1 < a.Length) host = a[++i];
        else pairing = a[i];
    }
    if (pairing is null)
    {
        var clip = ClipboardHelper.GetText();
        if (clip is not null && clip.StartsWith("remot://pair#", StringComparison.OrdinalIgnoreCase))
        { pairing = clip; Console.WriteLine("(从剪贴板读取配对串)"); }
        else { Console.Error.WriteLine("用法:remot pair [--name X] [--host H] <配对串>  (无参时读剪贴板)"); return 1; }
    }
    var p = PairingString.Decode(pairing);
    var h = string.IsNullOrEmpty(host) ? p.Host : host;
    var t = new Target(name ?? $"target-{h}", h, p.Port, p.Token, p.Fingerprint);
    using var c = new RemotClient(cfg); c.SaveTarget(t);
    Console.WriteLine($"✓ 已登记 {t.Name} → {t.Host}:{t.Port}");
    using var c2 = new RemotClient(cfg);
    try
    {
        var stub = new RemotService.RemotServiceClient(c2.GetChannel(t));
        await stub.CheckFileAsync(new FileCheckRequest { DestPath = "__ping__", Size = 0, Sha256 = "" },
            headers: new() { { "authorization", $"Bearer {p.Token}" } });
        Console.WriteLine("✓ 连通正常,token 有效");
    }
    catch (Grpc.Core.RpcException ex) { Console.WriteLine($"⚠ 连通自检:{ex.StatusCode} - {ex.Message}"); }
    Console.WriteLine($"下一步:remot run -t {t.Name} \"echo ok\"");
    return 0;
}

static async Task<int> RunCmd(string cfg, string[] a, CancellationToken ct)
{
    var (target, shell, rest) = ExtractOpts(a);
    if (target is null || rest.Count == 0)
    { Console.Error.WriteLine("用法:remot run -t <目标> [--shell pwsh|powershell|cmd] <命令...>"); return 1; }
    using var c = new RemotClient(cfg);
    var r = await c.RunCommandAsync(target, rest, shell, ct: ct);
    if (!r.Ok) { Console.Error.WriteLine(r.Error); return 1; }
    int rc = 0;
    foreach (var res in r.Value!)
    {
        Console.WriteLine($"[{res.Index}] exit={res.ExitCode}{(res.TimedOut ? " TIMEOUT" : "")}");
        if (!string.IsNullOrEmpty(res.Error)) Console.WriteLine($"ERROR: {res.Error}");
        Console.Write(res.Stdout);
        if (!string.IsNullOrEmpty(res.Stderr)) { Console.WriteLine("-- stderr --"); Console.WriteLine(res.Stderr); }
        if (res.ExitCode != 0) rc = 2;
    }
    return rc;
}

static async Task<int> Upload(string cfg, string[] a, CancellationToken ct)
{
    var (target, _, rest) = ExtractOpts(a);
    if (target is null) { Console.Error.WriteLine("用法:remot upload -t <目标> <src dst ...>"); return 1; }
    if (rest.Count % 2 != 0) { Console.Error.WriteLine("错误:upload 需 src dst 成对"); return 1; }
    var pairs = new List<(string, string)>();
    for (int i = 0; i + 1 < rest.Count; i += 2) pairs.Add((rest[i], rest[i + 1]));
    using var c = new RemotClient(cfg);
    var r = await c.UploadAsync(target, pairs, ct);
    if (!r.Ok) { Console.Error.WriteLine(r.Error); return 1; }
    int rc = 0;
    foreach (var x in r.Value!) { Console.WriteLine($"{x.Dest}: {(x.Ok ? "OK" : x.Error)} ({x.Bytes}B)"); if (!x.Ok) rc = 2; }
    return rc;
}

static async Task<int> Download(string cfg, string[] a, CancellationToken ct)
{
    var (target, _, rest) = ExtractOpts(a);
    if (target is null || rest.Count < 2) { Console.Error.WriteLine("用法:remot download -t <目标> <远程> <本地>"); return 1; }
    using var c = new RemotClient(cfg);
    var r = await c.DownloadAsync(target, rest[0], rest[1], ct);
    if (!r.Ok) { Console.Error.WriteLine(r.Error); return 1; }
    Console.WriteLine($"✓ 已下载 → {rest[1]}");
    return 0;
}

static async Task<int> PingCmd(string cfg, string[] a, CancellationToken ct)
{
    var (target, _, _) = ExtractOpts(a);
    if (target is null) { Console.Error.WriteLine("用法:remot ping -t <目标>"); return 1; }
    var tc = TargetsConfig.Load(cfg).Get(target);
    if (tc is null) { Console.Error.WriteLine($"未知目标:{target}"); return 1; }
    using var c = new RemotClient(cfg);
    var stub = new RemotService.RemotServiceClient(c.GetChannel(tc));
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await stub.CheckFileAsync(new FileCheckRequest { DestPath = "__ping__", Size = 0, Sha256 = "" },
            headers: new() { { "authorization", $"Bearer {tc.Token}" } }, cancellationToken: ct);
        Console.WriteLine($"✓ {target} 可达,token 有效 ({sw.ElapsedMilliseconds}ms)");
        return 0;
    }
    catch (Grpc.Core.RpcException ex) { Console.Error.WriteLine($"✗ {target}: {ex.StatusCode}"); return 1; }
}

static int InstallCli()
{
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Remot");
    Directory.CreateDirectory(dir);
    File.Copy(Environment.ProcessPath!, Path.Combine(dir, "remot.exe"), overwrite: true);
    var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
    if (!path.Contains(dir, StringComparison.OrdinalIgnoreCase))
        Environment.SetEnvironmentVariable("PATH", string.IsNullOrEmpty(path) ? dir : path + ";" + dir, EnvironmentVariableTarget.User);
    Console.WriteLine($"✓ remot 已安装到 PATH(新终端生效)");
    return 0;
}

static int TargetCmd(string cfg, string[] a)
{
    using var c = new RemotClient(cfg);
    if (a.Length == 0 || a[0] == "list") { foreach (var n in c.TargetNames) Console.WriteLine(n); return 0; }
    Console.Error.WriteLine("用法:remot target list"); return 1;
}

static (string? target, string shell, List<string> rest) ExtractOpts(string[] a)
{
    string? target = null; var shell = "powershell"; var rest = new List<string>();
    for (int i = 0; i < a.Length; i++)
    {
        if ((a[i] == "-t" || a[i] == "--target") && i + 1 < a.Length) { target = a[++i]; continue; }
        if (a[i] == "--shell" && i + 1 < a.Length) { shell = a[++i]; continue; }
        rest.Add(a[i]);
    }
    return (target, shell, rest);
}

static int Unknown(string c) { Console.Error.WriteLine($"未知命令:{c}"); return PrintHelp(); }
static bool Confirm() => (Console.ReadLine()?.Trim().ToLowerInvariant() ?? "y") is "" or "y" or "yes";
static void PauseExit() { Console.WriteLine("\n按任意键退出..."); Console.ReadKey(true); }

static int PrintHelp()
{
    Console.WriteLine("Remot — 远程执行 + 文件传输");
    Console.WriteLine("  remot pair [--name X] [--host H] [<配对串>]");
    Console.WriteLine("  remot run -t <目标> [--shell pwsh|powershell|cmd] [--deadline <秒>] <命令...>");
    Console.WriteLine("  remot upload -t <目标> <src dst [src dst ...]>");
    Console.WriteLine("  remot download -t <目标> <远程> <本地>");
    Console.WriteLine("  remot ping -t <目标>");
    Console.WriteLine("  remot install-cli              安装 remot 到 PATH");
    Console.WriteLine("  remot target list");
    Console.WriteLine("  [--config <路径>] [--deadline <秒>]");
    return 0;
}
