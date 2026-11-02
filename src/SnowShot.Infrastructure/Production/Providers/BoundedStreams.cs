using System.Buffers;
using System.Text;

namespace SnowShot.Infrastructure.Providers;

internal static class BoundedStreams
{
    public static async Task<byte[]> ReadAllAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var destination = new MemoryStream(Math.Min(maximumBytes, 81_920));
        var buffer = ArrayPool<byte>.Shared.Rent(16_384);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (destination.Length + read > maximumBytes) throw new InvalidDataException("Upstream response exceeded its configured bound.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return destination.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}

internal sealed class BoundedLineReader(Stream stream, int maximumLineBytes)
{
    private readonly byte[] _readBuffer = new byte[8_192];
    private int _offset;
    private int _count;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            if (_offset >= _count)
            {
                _count = await stream.ReadAsync(_readBuffer, cancellationToken);
                _offset = 0;
                if (_count == 0) return writer.WrittenCount == 0 ? null : Encoding.UTF8.GetString(writer.WrittenSpan).TrimEnd('\r');
            }
            var newline = Array.IndexOf(_readBuffer, (byte)'\n', _offset, _count - _offset);
            var end = newline >= 0 ? newline : _count;
            var length = end - _offset;
            if (writer.WrittenCount + length > maximumLineBytes) throw new InvalidDataException("SSE line exceeded its configured bound.");
            writer.Write(_readBuffer.AsSpan(_offset, length));
            _offset = newline >= 0 ? newline + 1 : _count;
            if (newline >= 0) return Encoding.UTF8.GetString(writer.WrittenSpan).TrimEnd('\r');
        }
    }
}
