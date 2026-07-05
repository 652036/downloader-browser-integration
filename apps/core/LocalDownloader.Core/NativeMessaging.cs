using System.Buffers.Binary;
using System.Text;

namespace LocalDownloader.Core;

public static class NativeMessaging
{
    public const int MaxMessageBytes = 1024 * 1024;

    public static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        var lengthBytesRead = await ReadExactlyOrEndAsync(stream, lengthBuffer, cancellationToken);
        if (lengthBytesRead == 0)
        {
            return null;
        }

        if (lengthBytesRead != lengthBuffer.Length)
        {
            throw new EndOfStreamException("Native messaging length prefix ended early.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length < 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException($"Native messaging payload length {length} is invalid.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return Encoding.UTF8.GetString(payload);
    }

    public static async Task WriteMessageAsync(Stream stream, string json, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidDataException($"Native messaging payload length {payload.Length} is invalid.");
        }

        var lengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, payload.Length);
        await stream.WriteAsync(lengthBuffer, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<int> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
            {
                return totalRead;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
