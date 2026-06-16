using Remot.Client.Files;
using Remot.Protocol;
using Xunit;

namespace Remot.Client.Tests;

public class FileChunkerTests
{
    [Fact]
    public async Task First_chunk_is_header_then_data()
    {
        var src = Path.GetTempFileName();
        await File.WriteAllBytesAsync(src, new byte[5_000_000]); // 跨多块
        var chunker = new FileChunker(chunkSize: 1024 * 1024);
        var list = new List<FileChunk>();
        await foreach (var c in chunker.StreamAsync(src, "C:\\dst", "sha", 5_000_000)) list.Add(c);

        Assert.Equal(FileChunk.KindOneofCase.Header, list[0].KindCase);
        Assert.Equal("C:\\dst", list[0].Header.DestPath);
        Assert.True(list.Count > 2);
        File.Delete(src);
    }
}
