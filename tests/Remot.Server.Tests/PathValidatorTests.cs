using Remot.Server.Files;
using Xunit;

namespace Remot.Server.Tests;

public class PathValidatorTests
{
    [Fact]
    public void Allows_any_absolute_when_no_allowlist()
    {
        var p = PathValidator.Validate(@"C:\some\dir\file.txt", Array.Empty<string>());
        Assert.Equal(@"C:\some\dir\file.txt", p);
    }

    [Fact]
    public void Resolves_traversal()
    {
        var p = PathValidator.Validate(@"C:\dir\..\dir2\file.txt", Array.Empty<string>());
        Assert.Equal(Path.GetFullPath(@"C:\dir2\file.txt"), p);
    }

    [Fact]
    public void Rejects_outside_allowlist()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            PathValidator.Validate(@"C:\Windows\System32\evil.dll", new[] { @"C:\allowed" }));
    }

    [Fact]
    public void Allows_under_allowlist()
    {
        var p = PathValidator.Validate(@"C:\allowed\sub\f.bin", new[] { @"C:\allowed" });
        Assert.Equal(Path.GetFullPath(@"C:\allowed\sub\f.bin"), p);
    }

    [Fact]
    public void Rejects_empty()
    {
        Assert.Throws<ArgumentException>(() => PathValidator.Validate("", Array.Empty<string>()));
    }
}
