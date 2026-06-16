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
}
