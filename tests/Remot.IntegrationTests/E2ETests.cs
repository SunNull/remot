using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Remot.Client;
using Remot.Client.Config;
using Remot.Protocol;
using Remot.Server.Execution;
using Remot.Server.Files;
using Remot.Server.Security;
using Remot.Server.Services;
using Remot.Server.Setup;
using Xunit;

namespace Remot.IntegrationTests;

/// <summary>端到端:进程内起真实 Kestrel gRPC 服务端 + RemotClient,验证命令执行与文件传输回路。</summary>
public class E2ETests : IAsyncLifetime
{
    private WebApplication? _app;
    private int _port;
    private readonly string _token = "e2e-token-" + Guid.NewGuid().ToString("N");
    private string _cfgPath = "";

    public RemotClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _port = 7000 + Random.Shared.Next(100, 900);
        var (cert, fingerprint) = CertGenerator.GenerateSelfSigned("localhost", "pw");

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddGrpc(options => options.Interceptors.Add<TokenInterceptor>());
        builder.Services.AddSingleton<TokenInterceptor>(_ => new TokenInterceptor(_token));
        builder.Services.AddSingleton<ICommandRunner, CommandRunner>();
        builder.Services.AddSingleton<IProcessFactory, ProcessFactory>();
        builder.Services.AddSingleton<Hasher>();
        builder.Services.AddSingleton<FileReceiver>();
        builder.Services.AddSingleton<FileSender>(sp => new FileSender(sp.GetRequiredService<Hasher>()));
        builder.Services.AddSingleton(new Remot.Server.Config.ServerConfig());   // 给 RemotServiceImpl 注入空配置(CommandGuard 用默认)
        builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(_port, lo =>
        {
            lo.Protocols = HttpProtocols.Http2;
            lo.UseHttps(cert);
        }));

        _app = builder.Build();
        _app.MapGrpcService<RemotServiceImpl>();
        _ = _app.RunAsync();
        await Task.Delay(400); // 等监听就绪

        _cfgPath = Path.Combine(Path.GetTempPath(), $"remot-e2e-{Guid.NewGuid()}.json");
        var cfg = new TargetsConfig();
        cfg.Upsert(new Target("e2e", "localhost", _port, _token, fingerprint));
        cfg.Save(_cfgPath);
        Client = new RemotClient(_cfgPath);
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (_app is not null) await _app.StopAsync();
        if (File.Exists(_cfgPath)) File.Delete(_cfgPath);
    }

    [Fact]
    public async Task Run_command_returns_exit_code_and_output()
    {
        var r = await Client.RunCommandAsync("e2e", new[] { "echo hello-remot" }, shell: "cmd");
        Assert.True(r.Ok, r.Error ?? "(no error msg)");
        Assert.Single(r.Value!);
        Assert.Equal(0, r.Value![0].ExitCode);
        Assert.Contains("hello-remot", r.Value![0].Stdout);
    }

    [Fact]
    public async Task Upload_then_content_matches()
    {
        var src = Path.GetTempFileName();
        await File.WriteAllTextAsync(src, "payload");
        var dst = Path.Combine(Path.GetTempPath(), "remot-e2e-dst-" + Guid.NewGuid() + ".txt");

        var up = await Client.UploadAsync("e2e", new[] { (src, dst) });
        Assert.True(up.Ok, up.Error ?? "(no error msg)");
        Assert.True(up.Value![0].Ok, up.Value![0].Error);
        Assert.Equal("payload", await File.ReadAllTextAsync(dst));

        File.Delete(src);
        if (File.Exists(dst)) File.Delete(dst);
    }

    [Fact]
    public async Task Batch_run_returns_per_command_results()
    {
        var r = await Client.RunCommandAsync("e2e", new[] { "echo one", "echo two" }, shell: "cmd");
        Assert.True(r.Ok);
        Assert.Equal(2, r.Value!.Count);
        Assert.Contains("one", r.Value[0].Stdout);
        Assert.Contains("two", r.Value[1].Stdout);
    }

    [Fact]
    public async Task Download_round_trips_with_integrity()   // H4:下载完整性 + 覆盖零测试缺口
    {
        var src = Path.GetTempFileName();
        await File.WriteAllTextAsync(src, "download-me 中文");
        var dst = Path.Combine(Path.GetTempPath(), "remot-e2e-dl-" + Guid.NewGuid() + ".txt");
        await Client.UploadAsync("e2e", new[] { (src, dst) });

        var local = Path.Combine(Path.GetTempPath(), "remot-e2e-local-" + Guid.NewGuid() + ".txt");
        var r = await Client.DownloadAsync("e2e", dst, local);
        Assert.True(r.Ok, r.Error ?? "(no error msg)");
        Assert.Equal("download-me 中文", await File.ReadAllTextAsync(local));

        File.Delete(src);
        if (File.Exists(dst)) File.Delete(dst);
        if (File.Exists(local)) File.Delete(local);
    }
}
