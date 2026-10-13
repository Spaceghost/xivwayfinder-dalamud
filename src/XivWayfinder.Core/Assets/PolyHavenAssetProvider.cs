using System.Text.Json;

namespace XivVisualAssets;

// Public API contract + required attribution/User-Agent: https://polyhaven.com/our-api
internal sealed class PolyHavenAssetProvider(AssetMetadataHttp http) : IVisualAssetSearchProvider
{
    public VisualAssetProviderInfo Info { get; } = new("polyhaven", "Poly Haven", "Powered by Poly Haven",
        System.Array.AsReadOnly(new[] { VisualAssetKind.All, VisualAssetKind.Model, VisualAssetKind.Material,
            VisualAssetKind.Texture, VisualAssetKind.Environment }));

    private sealed record Entry(VisualAsset Asset, string SearchText);
    private sealed record Catalog(string Type, DateTimeOffset Expires, Entry[] Entries);
    private readonly SemaphoreSlim gate = new(1, 1);
    private Catalog? cache;

    public async Task<VisualAssetSearchPage> SearchAsync(VisualAssetSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        request = request.Validated();
        cancellationToken.ThrowIfCancellationRequested();
        var offset = request.Offset();
        if (!Info.SupportedKinds.Contains(request.Kind)) return AssetJson.Unsupported(Info);
        var type = request.Kind switch
        {
            VisualAssetKind.Model => "models",
            VisualAssetKind.Material or VisualAssetKind.Texture => "textures",
            VisualAssetKind.Environment => "hdris",
            _ => "",
        };
        Entry[] catalog;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Only one catalogue is retained, for ten minutes; typing does not repeatedly fetch it.
            if (cache is null || cache.Type != type || cache.Expires <= DateTimeOffset.UtcNow)
            {
                var uri = new Uri("https://api.polyhaven.com/assets" + (type.Length == 0 ? "" : "?t=" + type));
                using var document = await http.GetAsync(uri, AssetMetadataHttp.CatalogByteLimit,
                    cancellationToken).ConfigureAwait(false);
                var entries = ParseCatalog(document.RootElement, cancellationToken);
                cache = new(type, DateTimeOffset.UtcNow.AddMinutes(10), entries);
            }
            catalog = cache.Entries;
        }
        finally { gate.Release(); }

        var words = request.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matched = new List<VisualAsset>();
        var seen = 0;
        var more = false;
        foreach (var entry in catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = entry.Asset.Kind;
            if (request.Kind != VisualAssetKind.All && kind != request.Kind &&
                !(request.Kind == VisualAssetKind.Texture && kind == VisualAssetKind.Material)) continue;
            if (!words.All(word => entry.SearchText.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
            if (seen++ < offset) continue;
            if (matched.Count == request.PageSize) { more = true; break; }
            matched.Add(entry.Asset);
        }
        return new(Info, matched.AsReadOnly(), more ? AssetJson.NextOffset(offset, matched.Count, int.MaxValue) : null);
    }

    private static Entry[] ParseCatalog(JsonElement root, CancellationToken cancellationToken)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Poly Haven returned an invalid catalogue.");
        var entries = new List<Entry>();
        var count = 0;
        foreach (var property in root.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > 6000) throw new InvalidDataException("Poly Haven catalogue exceeds the entry limit.");
            if (!AssetJson.Id(property.Name)) continue;
            var item = property.Value;
            var title = AssetJson.Text(item, "name");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var kind = AssetJson.Number(item, "type") switch
            {
                0 => VisualAssetKind.Environment,
                1 => VisualAssetKind.Material,
                2 => VisualAssetKind.Model,
                _ => (VisualAssetKind?)null,
            };
            if (kind is null) continue;
            var authors = AssetJson.Field(item, "authors");
            var author = authors.ValueKind == JsonValueKind.Object
                ? string.Join(", ", authors.EnumerateObject().Take(4).Select(p => AssetJson.Clean(p.Name, 80))) : "Poly Haven";
            var asset = new VisualAsset("polyhaven", property.Name, title, author, kind.Value,
                new Uri("https://polyhaven.com/a/" + property.Name),
                AssetJson.Https(AssetJson.Text(item, "thumbnail_url", 2048), "cdn.polyhaven.com"),
                AssetJson.Cc0, VisualAssetDownloadAccess.ProviderPage, FaceCount: AssetJson.Number(item, "polycount"));
            var tags = AssetJson.Field(item, "tags");
            var tagText = tags.ValueKind == JsonValueKind.Array
                ? string.Join(" ", tags.EnumerateArray().Take(24).Where(t => t.ValueKind == JsonValueKind.String)
                    .Select(t => AssetJson.Clean(t.GetString()!, 40))) : "";
            entries.Add(new(asset, $"{property.Name} {title} {AssetJson.Text(item, "category")} {tagText}"));
        }
        return entries.OrderBy(e => e.Asset.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Asset.Id, StringComparer.Ordinal).ToArray();
    }
}
