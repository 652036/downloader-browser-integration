using System.Threading.Channels;

namespace LocalDownloader.Tests.Relay;

/// <summary>
/// Minimal in-memory duplex byte stream pair for exercising bidirectional relay logic without
/// real pipes or sockets. Each side's Write feeds the other side's Read.
/// </summary>
public static class DuplexMemoryStream
{
    public static (Stream Left, Stream Right) CreatePair()
    {
        var leftToRight = Channel.CreateUnbounded<byte[]>();
        var rightToLeft = Channel.CreateUnbounded<byte[]>();

        var left = new ChannelStream(readChannel: rightToLeft, writeChannel: leftToRight);
        var right = new ChannelStream(readChannel: leftToRight, writeChannel: rightToLeft);
        return (left, right);
    }

    private sealed class ChannelStream : Stream
    {
        private readonly ChannelReader<byte[]> _reader;
        private readonly ChannelWriter<byte[]> _writer;
        private ReadOnlyMemory<byte> _pending;

        public ChannelStream(Channel<byte[]> readChannel, Channel<byte[]> writeChannel)
        {
            _reader = readChannel.Reader;
            _writer = writeChannel.Writer;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.IsEmpty)
            {
                bool has;
                try
                {
                    has = await _reader.WaitToReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }

                if (!has || !_reader.TryRead(out var chunk))
                {
                    return 0;
                }

                _pending = chunk;
            }

            var toCopy = Math.Min(buffer.Length, _pending.Length);
            _pending.Span[..toCopy].CopyTo(buffer.Span);
            _pending = _pending[toCopy..];
            return toCopy;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _writer.WriteAsync(buffer.ToArray(), cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _writer.TryComplete();
            }

            base.Dispose(disposing);
        }
    }
}
