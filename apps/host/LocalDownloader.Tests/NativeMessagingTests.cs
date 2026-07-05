using System.Text;
using LocalDownloader.Core;

namespace LocalDownloader.Tests;

public sealed class NativeMessagingTests
{
    [Fact]
    public async Task ReadMessageAsync_reads_little_endian_length_prefixed_json()
    {
        await using var stream = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
        stream.Write(BitConverter.GetBytes(payload.Length));
        stream.Write(payload);
        stream.Position = 0;

        var message = await NativeMessaging.ReadMessageAsync(stream, CancellationToken.None);

        Assert.Equal("{\"type\":\"ping\"}", message);
    }

    [Fact]
    public async Task WriteMessageAsync_writes_little_endian_length_prefixed_json()
    {
        await using var stream = new MemoryStream();

        await NativeMessaging.WriteMessageAsync(stream, "{\"type\":\"pong\"}", CancellationToken.None);

        var bytes = stream.ToArray();
        Assert.Equal(15, BitConverter.ToInt32(bytes.AsSpan(0, 4)));
        Assert.Equal("{\"type\":\"pong\"}", Encoding.UTF8.GetString(bytes, 4, 15));
    }
}
