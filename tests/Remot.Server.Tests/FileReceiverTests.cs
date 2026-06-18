using Google.Protobuf;
using Remot.Protocol;
using Remot.Server.Files;
using Xunit;

namespace Remot.Server.Tests;

public class FileReceiverTests
{
    private static async IAsyncEnumerable<FileChunk> Chunks(FileHeader h, params byte[][] datas)
    {
        yield return new FileChunk { Header = h };
        foreach (var d in datas)
            yield return new FileChunk { Data = ByteString.CopyFrom(d) };
    }

    [Fact]
    public async Task Writes_file_and_verifies_sha256()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remot-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "out.bin");
        var data = new byte[] { 1, 2, 3, 4 };
        var sha = await new Hasher().Sha256Async(new MemoryStream(data));
        var header = new FileHeader { DestPath = dest, ExpectedSha256 = sha, Size = data.Length, Overwrite = true };

        var r = await new FileReceiver(new Hasher()).ReceiveAsync(Chunks(header, data));

        Assert.True(r.Ok);
        Assert.Equal(data, await File.ReadAllBytesAsync(dest));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Mismatched_sha_returns_error_and_no_partial_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remot-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "out.bin");
        var header = new FileHeader { DestPath = dest, ExpectedSha256 = "deadbeef", Size = 4, Overwrite = true };

        var r = await new FileReceiver(new Hasher()).ReceiveAsync(Chunks(header, new byte[] { 1, 2, 3, 4 }));

        Assert.False(r.Ok);
        Assert.False(File.Exists(dest));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Empty_file_zero_bytes_succeeds()   // H3/H5:0 字节文件能传
    {
        var dir = Path.Combine(Path.GetTempPath(), "remot-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "empty.bin");
        var header = new FileHeader { DestPath = dest, ExpectedSha256 = "", Size = 0, Overwrite = true };

        var r = await new FileReceiver(new Hasher()).ReceiveAsync(Chunks(header));

        Assert.True(r.Ok);
        Assert.True(File.Exists(dest));
        Assert.Empty(await File.ReadAllBytesAsync(dest));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Negative_size_rejected()   // H3
    {
        var dir = Path.Combine(Path.GetTempPath(), "remot-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "x.bin");
        var header = new FileHeader { DestPath = dest, Size = -1, Overwrite = true };

        var r = await new FileReceiver(new Hasher()).ReceiveAsync(Chunks(header));

        Assert.False(r.Ok);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Nonempty_without_sha256_rejected()   // H3:Size>0 必须带 sha
    {
        var dir = Path.Combine(Path.GetTempPath(), "remot-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "x.bin");
        var data = new byte[] { 1, 2, 3, 4 };
        var header = new FileHeader { DestPath = dest, ExpectedSha256 = "", Size = data.Length, Overwrite = true };

        var r = await new FileReceiver(new Hasher()).ReceiveAsync(Chunks(header, data));

        Assert.False(r.Ok);
        Assert.Contains("sha256", r.Error);
        Assert.False(File.Exists(dest));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Size_mismatch_rejected()   // H3
    {
        var dir = Path.Combine(Path.GetTempPath(), "remot-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, "x.bin");
        var data = new byte[] { 1, 2, 3, 4 };
        var header = new FileHeader { DestPath = dest, ExpectedSha256 = "abcd", Size = 10, Overwrite = true };

        var r = await new FileReceiver(new Hasher()).ReceiveAsync(Chunks(header, data));

        Assert.False(r.Ok);
        Assert.Contains("大小不匹配", r.Error);
        Directory.Delete(dir, true);
    }
}
