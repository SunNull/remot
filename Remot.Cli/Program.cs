using Remot.Client;
using Remot.Client.Config;
using Remot.Client.Pairing;

return Run(args);

static int Run(string[] args)
{
    string configPath = DefaultConfigPath();
    var cmd = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--config" && i + 1 < args.Length) { configPath = args[++i]; continue; }
        cmd.Add(args[i]);
    }
    if (cmd.Count == 0) return PrintHelp();

    return cmd[0].ToLowerInvariant() switch
    {
        "pair" => Pair(configPath, cmd.Skip(1).ToArray()),
        "run" => RunCmd(configPath, cmd.Skip(1).ToArray()),
        "upload" => Upload(configPath, cmd.Skip(1).ToArray()),
        "download" => Download(configPath, cmd.Skip(1).ToArray()),
        "target" => TargetCmd(configPath, cmd.Skip(1).ToArray()),
        "help" or "-h" or "--help" => PrintHelp(),
        _ => Unknown(cmd[0])
    };
}

static string DefaultConfigPath() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".remot", "targets.json");

static int Pair(string cfg, string[] a)
{
    string? name = null, host = null, pairing = null;
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i] == "--name" && i + 1 < a.Length) name = a[++i];
        else if (a[i] == "--host" && i + 1 < a.Length) host = a[++i];
        else pairing = a[i];
    }
    if (pairing is null) { Console.Error.WriteLine("用法:remot pair [--name X] [--host H] <配对串>"); return 1; }
    var p = PairingString.Decode(pairing);
    var h = string.IsNullOrEmpty(host) ? p.Host : host;
    var t = new Target(name ?? $"target-{h}", h, p.Port, p.Token, p.Fingerprint);
    using var c = new RemotClient(cfg); c.SaveTarget(t);
    Console.WriteLine($"已登记目标 {t.Name} → {t.Host}:{t.Port}");
    return 0;
}

static int RunCmd(string cfg, string[] a)
{
    var (target, rest) = ExtractTarget(a);
    if (target is null || rest.Count == 0) { Console.Error.WriteLine("用法:remot run -t <目标> <命令...>"); return 1; }
    using var c = new RemotClient(cfg);
    var r = c.RunCommandAsync(target, rest).GetAwaiter().GetResult();
    if (!r.Ok) { Console.Error.WriteLine(r.Error); return 1; }
    int rc = 0;
    foreach (var res in r.Value!)
    {
        Console.WriteLine($"[{res.Index}] exit={res.ExitCode}{(res.TimedOut ? " TIMEOUT" : "")}");
        Console.WriteLine(res.Stdout);
        if (!string.IsNullOrEmpty(res.Stderr)) { Console.WriteLine("-- stderr --"); Console.WriteLine(res.Stderr); }
        if (res.ExitCode != 0) rc = 2;
    }
    return rc;
}

static int Upload(string cfg, string[] a)
{
    var (target, rest) = ExtractTarget(a);
    if (target is null) { Console.Error.WriteLine("用法:remot upload -t <目标> <src dst [src dst ...]>"); return 1; }
    var pairs = new List<(string, string)>();
    for (int i = 0; i + 1 < rest.Count; i += 2) pairs.Add((rest[i], rest[i + 1]));
    using var c = new RemotClient(cfg);
    var r = c.UploadAsync(target, pairs).GetAwaiter().GetResult();
    if (!r.Ok) { Console.Error.WriteLine(r.Error); return 1; }
    foreach (var x in r.Value!) Console.WriteLine($"{x.Dest}: {(x.Ok ? "OK" : x.Error)} ({x.Bytes}B)");
    return 0;
}

static int Download(string cfg, string[] a)
{
    var (target, rest) = ExtractTarget(a);
    if (target is null || rest.Count < 2) { Console.Error.WriteLine("用法:remot download -t <目标> <远程> <本地>"); return 1; }
    using var c = new RemotClient(cfg);
    var r = c.DownloadAsync(target, rest[0], rest[1]).GetAwaiter().GetResult();
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

static (string? target, List<string> rest) ExtractTarget(string[] a)
{
    string? target = null; var rest = new List<string>();
    for (int i = 0; i < a.Length; i++)
        if ((a[i] == "-t" || a[i] == "--target") && i + 1 < a.Length) target = a[++i];
        else rest.Add(a[i]);
    return (target, rest);
}

static int Unknown(string c) { Console.Error.WriteLine($"未知命令:{c}"); return PrintHelp(); }

static int PrintHelp()
{
    Console.WriteLine("Remot — 远程执行 + 文件传输");
    Console.WriteLine("  remot pair [--name X] [--host H] <配对串>");
    Console.WriteLine("  remot run -t <目标> <命令...>");
    Console.WriteLine("  remot upload -t <目标> <src dst [src dst ...]>");
    Console.WriteLine("  remot download -t <目标> <远程> <本地>");
    Console.WriteLine("  remot target list");
    Console.WriteLine("  [--config <路径>] 指定 targets.json");
    return 0;
}
