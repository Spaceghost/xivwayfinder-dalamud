using System.Net;
using System.Net.Http.Headers;
using System.Buffers.Binary;
using XivVisualAssets;

namespace XivWayfinder.Core.Tests;

public sealed class VisualAssetThumbnailTests
{
    private static readonly Uri Allowed = new("https://cdn.polyhaven.com/asset_img/thumbs/chair.png");
    [Fact]
    public async Task FetchesBoundedImageAndReportsDimensionsBeforeDecoder()
    {
        var calls = 0;
        using var loader = new VisualAssetThumbnailLoader(new Handler(request =>
        {
            calls++;
            Assert.Equal(new Uri(Allowed + "?format=png"), request.RequestUri);
            Assert.Contains("XivVisualAssets", request.Headers.UserAgent.ToString());
            return Response(Png(256, 128));
        }));
        var result = await loader.LoadAsync(Allowed);
        Assert.Equal((256, 128), (result.Width, result.Height));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PolyHavenRequestsPngWhilePreservingThumbnailSizingAndVersion()
    {
        using var loader = new VisualAssetThumbnailLoader(new Handler(request =>
        {
            Assert.Equal("?width=256&height=256&v=abcd&format=png", request.RequestUri!.Query);
            return Response(Png(193, 255));
        }));
        await loader.LoadAsync(new Uri(Allowed + "?width=256&format=webp&height=256&v=abcd"));
    }

    [Theory]
    [InlineData("http://cdn.polyhaven.com/a.png")]
    [InlineData("https://cdn.polyhaven.com.evil.invalid/a.png")]
    [InlineData("https://user@cdn.polyhaven.com/a.png")]
    [InlineData("https://127.0.0.1/a.png")]
    [InlineData("https://cdn.polyhaven.com:444/a.png")]
    [InlineData("relative.png")]
    public async Task RejectsUntrustedImageAddressBeforeHttp(string uri)
    {
        using var loader = new VisualAssetThumbnailLoader(new Handler(_ => throw new InvalidOperationException("Must not contact host")));
        await Assert.ThrowsAsync<ArgumentException>(() => loader.LoadAsync(new Uri(uri, UriKind.RelativeOrAbsolute)));
    }

    [Fact]
    public async Task RedirectResponseCannotBecomeAnImage()
    {
        using var loader = new VisualAssetThumbnailLoader(new Handler(_ => new(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("http://127.0.0.1/private") } }));
        await Assert.ThrowsAsync<HttpRequestException>(() => loader.LoadAsync(Allowed));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1025, 1)]
    [InlineData(1, 65535)]
    public async Task RefusesInvalidOrOversizedDecodedDimensions(int width, int height)
    {
        using var loader = new VisualAssetThumbnailLoader(new Handler(_ => Response(Png(width, height))));
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(Allowed));
    }

    [Fact]
    public async Task RefusesNonImageResponseEvenWithImageSuffix()
    {
        using var loader = new VisualAssetThumbnailLoader(new Handler(_ => Response("<html>login</html>"u8.ToArray(), "text/html")));
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(Allowed));
    }

    [Fact]
    public async Task ChecksActualStreamLengthWithoutContentLength()
    {
        using var loader = new VisualAssetThumbnailLoader(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new Unseekable(new byte[VisualAssetThumbnailLoader.MaximumBytes + 1])) };
            response.Content.Headers.ContentType = new("image/png");
            return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(Allowed));
    }

    [Fact]
    public async Task CancellationReachesRequestAndCannotProducePreview()
    {
        using var loader = new VisualAssetThumbnailLoader(new Handler(_ => Response(Png(1, 1))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadAsync(Allowed, cancellation.Token));
    }

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        return bytes;
    }
    private static HttpResponseMessage Response(byte[] bytes, string type = "image/png")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
    private sealed class Unseekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
