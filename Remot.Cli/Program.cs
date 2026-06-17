using Remot.Client;
using Remot.Client.Config;
using Remot.Client.Pairing;

// M9:支持 Ctrl+C 取消 + --deadline 超时
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--deadline" && i + 1 < args.Length && int.TryParse(args[i + 1], out var sec) && sec > 0)
        cts.CancelAfter(TimeSpan.FromSeconds(sec));
}
return await RunSafely(args, cts.Token);

static async Task<int> RunSafely(string[] args, CancellationToken ct)
{
    try { return await Run(args, ct); }
    catch (OperationCanceledException) { Console.Error.WriteLine("已取消"); return 130; }
    catch (Exception ex) { Console.Error.WriteLine($"错误: {ex.Message}"); return 1; }   // H7:顶层兜底
}

static async Task<int> Run(string[] args, CancellationToken ct)
{
    string configPath = DefaultConfigPath();
    var cmd = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--config" && i + 1 < args.Length) { configPath = args[++i]; continue; }
        if (args[i] == "--deadline") { if (i + 1 < args.Length) i++; continue; }   // 已在顶层处理,这里跳过值
        cmd.Add(args[i]);
    }
    if (cmd.Count == 0) return PrintHelp();

    return cmd[0].ToLowerInvariant() switch
    {
        "pair" => await Pair(configPath, cmd.Skip(1).ToArray()),
        "run" => await RunCmd(configPath, cmd.Skip(1).ToArray(), ct),
        "upload" => await Upload(configPath, cmd.Skip(1).ToArray(), ct),
        "download" => await Download(configPath, cmd.Skip(1).ToArray(), ct),
        "target" => TargetCmd(configPath, cmd.Skip(1).ToArray()),
        "help" or "-h" or "--help" => PrintHelp(),
        _ => Unknown(cmd[0])
    };
}

static string DefaultConfigPath() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remot", "targets.json");

static Task<int> Pair(string cfg, string[] a)
{
    string? name = null, host = null, pairing = null;
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i] == "--name" && i + 1 < a.Length) name = a[++i];
        else if (a[i] == "--host" && i + 1 < a.Length) host = a[++i];
        else pairing = a[i];
    }
    if (pairing is null) { Console.Error.WriteLine("用法:remot pair [--name X] [--host H] <配对串>"); return Task.FromResult(1); }
    var p = PairingString.Decode(pairing);
    var h = string.IsNullOrEmpty(host) ? p.Host : host;
    var t = new Target(name ?? $"target-{h}", h, p.Port, p.Token, p.Fingerprint);
    using var c = new RemotClient(cfg); c.SaveTarget(t);
    Console.WriteLine($"已登记目标 {t.Name} → {t.Host}:{t.Port}");
    return Task.FromResult(0);
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
    if (target is null) { Console.Error.WriteLine("用法:remot upload -t <目标> <src dst [src dst ...]>"); return 1; }
    if (rest.Count % 2 != 0) { Console.Error.WriteLine("错误:upload 需要 src dst 成对(当前参数为奇数)"); return 1; }   // L9
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
    Console.WriteLine($"已下载 → {rest[1]}");
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

static int PrintHelp()
{
    Console.WriteLine("Remot — 远程执行 + 文件传输");
    Console.WriteLine("  remot pair [--name X] [--host H] <配对串>");
    Console.WriteLine("  remot run -t <目标> [--shell pwsh|powershell|cmd] [--deadline <秒>] <命令...>");
    Console.WriteLine("  remot upload -t <目标> <src dst [src dst ...]>");
    Console.WriteLine("  remot download -t <目标> <远程> <本地>");
    Console.WriteLine("  remot target list");
    Console.WriteLine("  [--config <路径>] [--deadline <秒>]   Ctrl+C 可取消");
    return 0;
}
