using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SnowShot.Infrastructure.Accounts;

namespace SnowShotApi.Tests.Domain;

public sealed class CaptchaImageTests
{
    [Fact]
    public void RandomChallengesHaveFourCharacters()
    {
        for (var sample = 0; sample < 16; sample++)
        {
            var image = CaptchaImage.Create();
            Assert.Equal(4, image.Answer.Length);
            Assert.All(image.Answer, character => Assert.Contains(character, "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"));
            var decoded = Decode(image.Png);
            Assert.Equal(152, decoded.Width);
            Assert.Equal(64, decoded.Height);
            AssertMargins(decoded);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void PngContainsCompleteGlyphsAtBothVerticalExtremes(int verticalOffset)
    {
        var image = Decode(CaptchaImage.Render("8888", [verticalOffset, verticalOffset, verticalOffset, verticalOffset]));
        var bands = GlyphBands(image);
        Assert.Equal(4, bands.Count);
        foreach (var (left, right) in bands)
        {
            var pixels = Enumerable.Range(0, image.Height).SelectMany(y =>
                Enumerable.Range(left, right - left + 1).Where(x => image.IsGlyph(x, y)).Select(x => (X: x, Y: y))).ToArray();
            // The reference 5x7 "8" has 17 filled cells, each rendered as a complete 4x4 block.
            Assert.Equal(17 * 16, pixels.Length);
            Assert.Equal(28, pixels.Max(p => p.Y) - pixels.Min(p => p.Y) + 1);
            Assert.Equal(14 + verticalOffset, pixels.Min(p => p.Y));
        }
        AssertMargins(image);
    }

    [Theory]
    [InlineData("8", 50)]
    [InlineData("8888", 152)]
    [InlineData("888888", 220)]
    public void CanvasWidthFollowsCharacterCountWithoutLosingGlyphs(string answer, int expectedWidth)
    {
        var image = Decode(CaptchaImage.Render(answer, Enumerable.Range(0, answer.Length).Select(i => i % 2 * 8).ToArray()));
        Assert.Equal(expectedWidth, image.Width);
        Assert.Equal(answer.Length, GlyphBands(image).Count);
        var count = Enumerable.Range(0, image.Height).Sum(y => Enumerable.Range(0, image.Width).Count(x => image.IsGlyph(x, y)));
        Assert.Equal(answer.Length * 17 * 16, count);
        AssertMargins(image);
    }

    private static void AssertMargins(RgbImage image)
    {
        var pixels = Enumerable.Range(0, image.Height).SelectMany(y =>
            Enumerable.Range(0, image.Width).Where(x => image.IsGlyph(x, y)).Select(x => (X: x, Y: y))).ToArray();
        Assert.NotEmpty(pixels);
        Assert.True(pixels.Min(p => p.X) >= 14, "The glyphs need clear left padding.");
        Assert.True(image.Width - 1 - pixels.Max(p => p.X) >= 14, "The glyphs need clear right padding.");
        Assert.True(pixels.Min(p => p.Y) >= 14, "The top glyph rows must fit with clear padding.");
        Assert.True(image.Height - 1 - pixels.Max(p => p.Y) >= 14, "The bottom glyph rows must fit with clear padding.");
    }

    private static List<(int Left, int Right)> GlyphBands(RgbImage image)
    {
        var result = new List<(int, int)>();
        int? start = null;
        var last = 0;
        for (var x = 0; x < image.Width; x++)
        {
            if (!Enumerable.Range(0, image.Height).Any(y => image.IsGlyph(x, y))) continue;
            // A slight row shear can leave an internal blank column; separate only the wider spaces between characters.
            if (start is { } left && x - last >= 8) { result.Add((left, last)); start = null; }
            start ??= x;
            last = x;
        }
        if (start is { } finalLeft) result.Add((finalLeft, last));
        return result;
    }

    private static RgbImage Decode(byte[] png)
    {
        Assert.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        var width = 0; var height = 0;
        using var compressed = new MemoryStream();
        for (var offset = 8; offset + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            Assert.InRange(length, 0, png.Length - offset - 12);
            var tag = Encoding.ASCII.GetString(png, offset + 4, 4);
            if (tag == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 8, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 12, 4));
                Assert.Equal(8, png[offset + 16]);
                Assert.Equal(2, png[offset + 17]);
            }
            else if (tag == "IDAT") compressed.Write(png, offset + 8, length);
            offset += length + 12;
        }
        Assert.True(width > 0 && height > 0);
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var rows = new MemoryStream(); zlib.CopyTo(rows);
        var data = rows.ToArray();
        Assert.Equal(height * (width * 3 + 1), data.Length);
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, data[y * (width * 3 + 1)]);
            data.AsSpan(y * (width * 3 + 1) + 1, width * 3).CopyTo(pixels.AsSpan(y * width * 3));
        }
        return new(width, height, pixels);
    }

    private sealed record RgbImage(int Width, int Height, byte[] Pixels)
    {
        internal bool IsGlyph(int x, int y)
        {
            var offset = (y * Width + x) * 3;
            return Pixels[offset] == 30 && Pixels[offset + 1] == 63 && Pixels[offset + 2] == 99;
        }
    }
}
