using System.Net;
using XivWayfinder.Core;

namespace XivVisualAssets;

public sealed record VisualAssetThumbnail(byte[] Data, int Width, int Height);

/// <summary>Fetches one explicitly selected repository preview, never a model archive.</summary>
public sealed class VisualAssetThumbnailLoader : IDisposable
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const int MaximumDimension = 1024;
    private readonly HttpClient client;
    public VisualAssetThumbnailLoader() : this(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    }) { }

    public VisualAssetThumbnailLoader(HttpMessageHandler handler)
    {
        client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XivVisualAssets/0.1");
    }

    public async Task<VisualAssetThumbnail> LoadAsync(Uri source, CancellationToken cancellationToken = default)
    {
        if (!source.IsAbsoluteUri || source.AbsoluteUri.Length > 2048 || !AssetJson.IsHttps(source,
            "cdn.polyhaven.com", "media.sketchfab.com", "acg-media.struffelproductions.com", "ambientcg.com"))
            throw new ArgumentException("Unsupported repository preview host.", nameof(source));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        // Poly Haven's CDN may return WebP even for its API's .png URL and an
        // image/png Accept header. Request its verified PNG output explicitly.
        if (source.IdnHost.Equals("cdn.polyhaven.com", StringComparison.OrdinalIgnoreCase))
        {
            var query = source.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !Uri.UnescapeDataString(part.Split('=', 2)[0]).Equals("format", StringComparison.OrdinalIgnoreCase));
            source = new UriBuilder(source) { Query = string.Join('&', query.Append("format=png")) }.Uri;
        }
        using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var mime = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (mime is not ("image/png" or "image/jpeg")) throw new InvalidDataException("Repository preview is not a PNG or JPEG image.");
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Repository preview is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > MaximumBytes) throw new InvalidDataException("Repository preview is too large.");
            output.Write(buffer, 0, count);
        }
        timeout.Token.ThrowIfCancellationRequested();
        var data = output.ToArray();
        var size = GuideImageBounds.Read(mime, data);
        if (size.Width > MaximumDimension || size.Height > MaximumDimension)
            throw new InvalidDataException("Repository preview dimensions are too large.");
        return new(data, size.Width, size.Height);
    }

    public void Dispose() => client.Dispose();
}
