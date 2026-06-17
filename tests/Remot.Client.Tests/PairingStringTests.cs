using Remot.Client.Pairing;
using Xunit;

namespace Remot.Client.Tests;

public class PairingStringTests
{
    [Fact]
    public void Round_trip()
    {
        var s = PairingString.Encode("192.168.1.20", 7070, "tok-abc", "fp-xyz", "test-29298");
        Assert.StartsWith("remot://pair#", s);
        var p = PairingString.Decode(s);
        Assert.Equal("test-29298", p.Name);
        Assert.Equal("192.168.1.20", p.Host);
        Assert.Equal(7070, p.Port);
        Assert.Equal("tok-abc", p.Token);
        Assert.Equal("fp-xyz", p.Fingerprint);
    }

    [Fact]
    public void Bad_scheme_throws()
    {
        Assert.Throws<FormatException>(() => PairingString.Decode("http://nope#x"));
    }

    [Fact]
    public void Empty_required_fields_throw()   // M9
    {
        var json = "{\"name\":\"\",\"host\":\"\",\"port\":7070,\"token\":\"\",\"fingerprint\":\"\"}";
        var bad = "remot://pair#" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Throws<FormatException>(() => PairingString.Decode(bad));
    }
}
