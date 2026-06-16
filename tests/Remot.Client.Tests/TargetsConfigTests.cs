using Remot.Client.Config;
using Xunit;

namespace Remot.Client.Tests;

public class TargetsConfigTests
{
    [Fact]
    public void Upsert_then_load_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), "remot-tgt-" + Guid.NewGuid() + ".json");
        var cfg = new TargetsConfig();
        cfg.Upsert(new Target("test", "10.0.0.1", 7070, "tok", "fp"));
        cfg.Save(path);

        var loaded = TargetsConfig.Load(path);
        var t = loaded.Get("test");
        Assert.NotNull(t);
        Assert.Equal("10.0.0.1", t!.Host);
        Assert.Equal("fp", t.CertFingerprint);
        File.Delete(path);
    }
}
