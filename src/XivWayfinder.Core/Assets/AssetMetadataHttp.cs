using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace XivVisualAssets;

internal sealed class AssetMetadataHttp : IDisposable
{
    internal const int PageByteLimit = 1024 * 1024;
    internal const int CatalogByteLimit = 4 * 1024 * 1024;
    private static readonly string[] Hosts = ["api.polyhaven.com", "ambientcg.com", "api.sketchfab.com"];
    private readonly HttpClient client;

    internal AssetMetadataHttp() : this(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    }) { }

    internal AssetMetadataHttp(HttpMessageHandler handler)
    {
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XivVisualAssets/0.1");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    internal async Task<JsonDocument> GetAsync(Uri uri, int byteLimit, CancellationToken cancellationToken)
    {
        if (!AssetJson.IsHttps(uri, Hosts) || byteLimit is < 1 or > CatalogByteLimit)
            throw new ArgumentException("Unsupported metadata endpoint.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        // Do not follow server-provided next URLs or redirects, including redirects to private services.
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > byteLimit)
            throw new InvalidDataException("Provider metadata exceeds the response size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > byteLimit)
                throw new InvalidDataException("Provider metadata exceeds the response size limit.");
            output.Write(buffer, 0, count);
        }
        timeout.Token.ThrowIfCancellationRequested();
        return JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length),
            new JsonDocumentOptions { MaxDepth = 32 });
    }

    public void Dispose() => client.Dispose();
}

internal static class AssetJson
{
    internal static readonly VisualAssetLicense Cc0 =
        new("CC0 1.0", new Uri("https://creativecommons.org/publicdomain/zero/1.0/"));

    internal static JsonElement Field(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? value : default;

    internal static string Text(JsonElement item, string key, int length = 256) =>
        Field(item, key) is { ValueKind: JsonValueKind.String } value ? Clean(value.GetString() ?? "", length) : "";

    internal static string Clean(string value, int length) =>
        new(value.Take(length).Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    internal static long? Number(JsonElement item, string key) =>
        Field(item, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var n) && n >= 0 ? n : null;

    internal static bool True(JsonElement item, string key) => Field(item, key).ValueKind == JsonValueKind.True;

    internal static bool Id(string value) => value.Length is > 0 and <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    internal static bool IsHttps(Uri uri, params string[] hosts) => uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    internal static Uri? Https(string value, params string[] hosts) => value.Length <= 2048 &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsHttps(uri, hosts) ? uri : null;

    internal static JsonElement Array(JsonElement item, string key)
    {
        var value = Field(item, key);
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Provider returned an invalid asset list.");
        return value;
    }

    internal static VisualAssetSearchPage Unsupported(VisualAssetProviderInfo info) =>
        new(info, System.Array.Empty<VisualAsset>(), null, "This provider does not offer this asset kind.");

    internal static string? NextOffset(int offset, int count, int total) => count > 0 &&
        offset + count < total && offset + count <= VisualAssetSearchRequest.MaxOffset
        ? (offset + count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
}
