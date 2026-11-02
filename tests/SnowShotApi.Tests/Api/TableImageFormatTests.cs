using System.Net;
using System.Net.Http.Headers;
using SnowShot.Domain;
using SnowShot.Application;

namespace SnowShotApi.Tests.Api;

public sealed class RecognitionImageFormatTests
{
    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    public async Task TableAcceptsActualImageSignatureAndPreservesSettlement(string format)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        using var form = ImageForm(format);
        using var response = await client.PostAsync("/api/v1/table/extract", form, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, factory.Table.Invocations);
        var expectedFormat = format switch { "jpeg" => RecognitionImageFormat.Jpeg, "png" => RecognitionImageFormat.Png, _ => RecognitionImageFormat.Webp };
        Assert.Equal(expectedFormat, factory.Table.LastCommand?.Format);
        Assert.NotEmpty(factory.Table.LastCommand!.ImageData.ToArray());
        Assert.Equal(new NanoYuan(30_000_000), Assert.Single(factory.Ledger.Settlements).ReportedPublicCost);
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    public async Task LatexAcceptsActualImageSignatureAndPreservesSettlement(string format)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        using var form = ImageForm(format);
        using var response = await client.PostAsync("/api/v1/latex/extract", form, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, factory.Latex.Invocations);
        var expectedFormat = format switch { "jpeg" => RecognitionImageFormat.Jpeg, "png" => RecognitionImageFormat.Png, _ => RecognitionImageFormat.Webp };
        Assert.Equal(expectedFormat, factory.Latex.LastCommand?.Format);
        Assert.NotEmpty(factory.Latex.LastCommand!.ImageData.ToArray());
        Assert.Equal(new NanoYuan(15_000_000), Assert.Single(factory.Ledger.Settlements).ReportedPublicCost);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("latex")]
    public async Task UnsupportedSignatureIsRejectedBeforeBilling(string resource)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateAnonymousClient();
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent("GIF89a"u8.ToArray());
        image.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
        form.Add(image, "image", "spoofed.png");
        using var response = await client.PostAsync($"/api/v1/{resource}/extract", form, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Table.Invocations + factory.Latex.Invocations);
        Assert.Empty(factory.Ledger.Reservations);
    }

    private static MultipartFormDataContent ImageForm(string format)
    {
        byte[] bytes = format switch
        {
            "jpeg" => [0xff, 0xd8, 0xff, 0xe0],
            "png" => [137, 80, 78, 71, 13, 10, 26, 10],
            _ => "RIFF0000WEBP"u8.ToArray(),
        };
        var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(bytes);
        // The gateway detects the signature, not this untrusted declaration or filename.
        image.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        form.Add(image, "image", "upload.bin");
        return form;
    }
}
