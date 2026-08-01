using System.Buffers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace SnowShot.Api;

internal static class TableMultipartReader
{
    internal const long MultipartOverheadBytes = 1024 * 1024;

    public static async Task<PooledImageBuffer> ReadAsync(
        HttpRequest request,
        long maximumImageBytes,
        CancellationToken cancellationToken)
    {
        if (maximumImageBytes is <= 0 or > int.MaxValue) throw new InvalidOperationException("Table upload limit must fit in a managed buffer.");
        var maximumRequestBytes = checked(maximumImageBytes + MultipartOverheadBytes);
        if (request.ContentLength > maximumRequestBytes) throw new TablePayloadTooLargeException();

        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = maximumRequestBytes;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new TableMultipartException();
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 256) throw new TableMultipartException();

        var countingBody = new MaximumLengthReadStream(request.Body, maximumRequestBytes);
        var reader = new MultipartReader(boundary, countingBody)
        {
            BodyLengthLimit = maximumImageBytes,
            HeadersCountLimit = 16,
            HeadersLengthLimit = 16 * 1024,
        };
        PooledImageBuffer? accepted = null;
        try
        {
            var section = await reader.ReadNextSectionAsync(cancellationToken);
            if (section is null || !IsImageFile(section.ContentDisposition)) throw new TableMultipartException();
            accepted = await PooledImageBuffer.ReadAsync(section.Body, (int)maximumImageBytes, cancellationToken);
            if (accepted.Length == 0 || !HasWebpSignature(accepted.Memory.Span)) throw new TableMultipartException();
            if (await reader.ReadNextSectionAsync(cancellationToken) is not null) throw new TableMultipartException();
            await DrainAsync(countingBody, cancellationToken);
            return accepted;
        }
        catch (TablePayloadTooLargeException)
        {
            accepted?.Dispose();
            throw;
        }
        catch (InvalidDataException exception) when (IsLimitFailure(exception))
        {
            accepted?.Dispose();
            throw new TablePayloadTooLargeException(exception);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or FormatException)
        {
            accepted?.Dispose();
            throw new TableMultipartException(exception);
        }
        catch
        {
            accepted?.Dispose();
            throw;
        }
    }

    private static bool IsImageFile(string? value)
    {
        if (!ContentDispositionHeaderValue.TryParse(value, out var disposition) ||
            !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)) return false;
        var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
        var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value ??
            HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
        return string.Equals(name, "image", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(fileName);
    }

    private static bool HasWebpSignature(ReadOnlySpan<byte> value) => value.Length >= 12 &&
        value[..4].SequenceEqual("RIFF"u8) && value.Slice(8, 4).SequenceEqual("WEBP"u8);

    private static bool IsLimitFailure(InvalidDataException exception) =>
        exception.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase);

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken) != 0) { }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}

internal sealed class PooledImageBuffer : IDisposable
{
    private byte[]? _buffer;
    private PooledImageBuffer(byte[] buffer, int length) { _buffer = buffer; Length = length; }
    public int Length { get; }
    public ReadOnlyMemory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(PooledImageBuffer))).AsMemory(0, Length);

    public static async Task<PooledImageBuffer> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(maximumBytes);
        var length = 0;
        try
        {
            while (length < maximumBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, maximumBytes - length), cancellationToken);
                if (read == 0) return new(buffer, length);
                length += read;
            }
            if (await stream.ReadAsync(new byte[1], cancellationToken) != 0) throw new TablePayloadTooLargeException();
            return new(buffer, length);
        }
        catch
        {
            Array.Clear(buffer, 0, length);
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is null) return;
        Array.Clear(buffer, 0, Length);
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

internal sealed class MaximumLengthReadStream(Stream inner, long maximumBytes) : Stream
{
    private long _read;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));
    private int Count(int value)
    {
        _read = checked(_read + value);
        if (_read > maximumBytes) throw new TablePayloadTooLargeException();
        return value;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class TableMultipartException(Exception? inner = null) : Exception("Invalid table multipart request.", inner);
internal sealed class TablePayloadTooLargeException(Exception? inner = null) : Exception("Table upload exceeds its configured limit.", inner);
