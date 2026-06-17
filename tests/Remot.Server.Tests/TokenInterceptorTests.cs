using Remot.Server.Security;
using Xunit;

namespace Remot.Server.Tests;

public class TokenInterceptorTests
{
    [Fact]
    public void Empty_token_rejected_at_construction()   // C3
    {
        Assert.Throws<ArgumentException>(() => new TokenInterceptor(""));
        Assert.Throws<ArgumentException>(() => new TokenInterceptor("   "));
    }

    [Fact]
    public void Valid_token_constructs()
    {
        var ti = new TokenInterceptor(new string('a', 64));
        Assert.NotNull(ti);
    }
}
