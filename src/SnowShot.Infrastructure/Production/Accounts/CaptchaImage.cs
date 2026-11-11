using System.Security.Cryptography;

namespace SnowShot.Infrastructure.Accounts;

internal static class CaptchaImage
{
    private const int ChallengeLength = 4;
    private const int GlyphColumns = 5;
    private const int GlyphRows = 7;
    private const int PixelScale = 4;
    private const int MaximumHorizontalShift = 2;
    private const int MaximumVerticalOffset = 8;
    private const int HorizontalPadding = 14;
    private const int VerticalPadding = 14;
    private const int CharacterGap = 12;
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private static readonly string[] Glyphs =
    ["01110100010000100010001000100011111", "11110000010000101110000010000111110", "00010001100101010010111110001000010", "11111100001111000001000011000101110", "00110010001000011110100011000101110", "11111000010001000100010000100001000", "01110100011000101110100011000101110", "01110100011000101111000010001001100", "01110100011000111111100011000110001", "11110100011000111110100011000111110", "01111100001000010000100001000001111", "11110100011000110001100011000111110", "11111100001000011110100001000011111", "11111100001000011110100001000010000", "01111100001000010111100011000101111", "10001100011000111111100011000110001", "00111000100001000010000101001001100", "10001100101010011000101001001010001", "10000100001000010000100001000011111", "10001110111010110101100011000110001", "10001110011010110011100011000110001", "11110100011000111110100001000010000", "01110100011000110001101011001001101", "11110100011000111110101001001010001", "01111100001000001110000010000111110", "11111001000010000100001000010000100", "10001100011000110001100011000101110", "10001100011000110001100010101000100", "10001100011000110101101011010101010", "10001100010101000100010101000110001", "10001100010101000100001000010000100", "11111000010001000100010001000011111"];

    internal static (string Answer, byte[] Png) Create()
    {
        var answer = new string(Enumerable.Range(0, ChallengeLength)
            .Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        var verticalOffsets = Enumerable.Range(0, answer.Length)
            .Select(_ => RandomNumberGenerator.GetInt32(MaximumVerticalOffset + 1)).ToArray();
        return (answer, Render(answer, verticalOffsets));
    }

    internal static byte[] Render(string answer, ReadOnlySpan<int> verticalOffsets)
    {
        ArgumentException.ThrowIfNullOrEmpty(answer);
        if (verticalOffsets.Length != answer.Length)
            throw new ArgumentException("Each character requires a vertical offset.", nameof(verticalOffsets));

        const int glyphWidth = GlyphColumns * PixelScale + MaximumHorizontalShift;
        const int glyphHeight = GlyphRows * PixelScale;
        var width = checked(HorizontalPadding * 2 + answer.Length * glyphWidth +
            (answer.Length - 1) * CharacterGap);
        const int height = VerticalPadding * 2 + glyphHeight + MaximumVerticalOffset;
        var pixels = Enumerable.Repeat((byte)246, checked(width * height * 3)).ToArray();
        void Dot(int x, int y, byte r, byte g, byte b)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                throw new InvalidOperationException("CAPTCHA drawing exceeds its calculated canvas.");
            var offset = (y * width + x) * 3;
            pixels[offset] = r;
            pixels[offset + 1] = g;
            pixels[offset + 2] = b;
        }

        // Background noise must not erase pixels from the readable characters.
        for (var i = 0; i < width * height / 22; i++)
            Dot(RandomNumberGenerator.GetInt32(width), RandomNumberGenerator.GetInt32(height),
                140, 166, 186);

        for (var i = 0; i < answer.Length; i++)
        {
            var glyphIndex = Alphabet.IndexOf(answer[i], StringComparison.Ordinal);
            if (glyphIndex < 0)
                throw new ArgumentException("The answer contains an unsupported character.", nameof(answer));
            ArgumentOutOfRangeException.ThrowIfNegative(verticalOffsets[i]);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(verticalOffsets[i], MaximumVerticalOffset);
            var glyph = Glyphs[glyphIndex];
            var originX = HorizontalPadding + i * (glyphWidth + CharacterGap);
            var originY = VerticalPadding + verticalOffsets[i];
            for (var y = 0; y < GlyphRows; y++)
                for (var x = 0; x < GlyphColumns; x++)
                    if (glyph[y * GlyphColumns + x] == '1')
                        for (var dy = 0; dy < PixelScale; dy++)
                            for (var dx = 0; dx < PixelScale; dx++)
                                Dot(originX + x * PixelScale + dx + y % (MaximumHorizontalShift + 1),
                                    originY + y * PixelScale + dy, 30, 63, 99);
        }
        return PngImage.EncodeRgb(pixels, width, height);
    }
}
