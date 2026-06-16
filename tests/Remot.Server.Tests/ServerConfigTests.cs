using Remot.Server.Config;
using Xunit;

namespace Remot.Server.Tests;

public class ServerConfigTests
{
    [Fact]
    public void Round_trip_through_json()
    {
        var path = Path.Combine(Path.GetTempPath(), "remot-cfg-" + Guid.NewGuid() + ".json");
        var cfg = ServerConfig.CreateNew(port: 7070, token: "tok", certPath: "c.pfx", certPassword: "pw");
        cfg.Save(path);
        var loaded = ServerConfig.Load(path);
        Assert.Equal(7070, loaded.Port);
        Assert.Equal("tok", loaded.Token);
        Assert.Equal("c.pfx", loaded.CertPath);
        Assert.Equal("pw", loaded.CertPassword);
        File.Delete(path);
    }

    [Fact]
    public void NewToken_is_64_hex_chars()
    {
        var t = ServerConfig.NewToken();
        Assert.Equal(64, t.Length);
        Assert.Matches("^[0-9a-f]{64}$", t);
    }
}
