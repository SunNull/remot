using Remot.Server.Files;
using Xunit;

namespace Remot.Server.Tests;

public class HasherTests
{
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public async Task Known_vectors(string input, string expected)
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(input));
        var h = new Hasher();
        Assert.Equal(expected, await h.Sha256Async(ms));
    }
}
