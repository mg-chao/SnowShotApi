using System.Buffers.Binary;
using System.IO.Compression;

namespace SnowShot.Infrastructure.Accounts;

internal static class PngImage
{
    internal static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    internal static byte[] EncodeRgb(ReadOnlySpan<byte> pixels, int width, int height)
    {
        var stride = width * 3;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(pixels.Slice(y * stride, stride));
            }
        }
        using var png = new MemoryStream();
        png.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var buffer = new byte[type.Length + data.Length];
        type.CopyTo(buffer);
        data.CopyTo(buffer.AsSpan(type.Length));
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(value, data.Length);
        output.Write(value);
        output.Write(buffer);
        var crc = uint.MaxValue;
        foreach (var item in buffer)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320U : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc);
        output.Write(value);
    }
}
