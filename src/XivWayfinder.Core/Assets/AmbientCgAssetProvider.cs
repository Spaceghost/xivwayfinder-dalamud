namespace XivVisualAssets;

// v3 (v2's legacy type filter is no longer reliable): https://docs.ambientcg.com/api/v3/assets/
// All assets and preview images are CC0: https://docs.ambientcg.com/license/
internal sealed class AmbientCgAssetProvider(AssetMetadataHttp http) : IVisualAssetSearchProvider
{
    public VisualAssetProviderInfo Info { get; } = new("ambientcg", "ambientCG", "Assets from ambientCG",
        Array.AsReadOnly(new[] { VisualAssetKind.All, VisualAssetKind.Model, VisualAssetKind.Material,
            VisualAssetKind.Texture, VisualAssetKind.Environment }));

    public async Task<VisualAssetSearchPage> SearchAsync(VisualAssetSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        request = request.Validated();
        cancellationToken.ThrowIfCancellationRequested();
        var offset = request.Offset();
        if (!Info.SupportedKinds.Contains(request.Kind)) return AssetJson.Unsupported(Info);
        var type = request.Kind switch
        {
            VisualAssetKind.Model => "3d-model",
            VisualAssetKind.Material => "material,substance",
            VisualAssetKind.Texture => "plain-image,decal,atlas,brush",
            VisualAssetKind.Environment => "hdri,hdri-element",
            _ => "",
        };
        var uri = new Uri("https://ambientcg.com/api/v3/assets?q=" + Uri.EscapeDataString(request.Query) +
            "&type=" + Uri.EscapeDataString(type) + "&sort=popular&limit=" + request.PageSize + "&offset=" + offset +
            "&include=type,title,url,thumbnails");
        using var document = await http.GetAsync(uri, AssetMetadataHttp.PageByteLimit,
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var assets = AssetJson.Array(root, "assets");
        if (assets.GetArrayLength() > request.PageSize)
            throw new InvalidDataException("ambientCG returned too many results.");
        var results = new List<VisualAsset>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in assets.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = AssetJson.Text(item, "id", 129);
            var title = AssetJson.Text(item, "title");
            var kind = AssetJson.Text(item, "type") switch
            {
                "3d-model" => VisualAssetKind.Model,
                "material" or "substance" => VisualAssetKind.Material,
                "plain-image" or "decal" or "atlas" or "brush" => VisualAssetKind.Texture,
                "hdri" or "hdri-element" => VisualAssetKind.Environment,
                _ => (VisualAssetKind?)null,
            };
            if (!AssetJson.Id(id) || !ids.Add(id) || string.IsNullOrWhiteSpace(title) || kind is null ||
                request.Kind != VisualAssetKind.All && request.Kind != kind) continue;
            var thumbnail = AssetJson.Text(AssetJson.Field(item, "thumbnails"), "256-PNG", 2048);
            results.Add(new("ambientcg", id, title, "ambientCG", kind.Value,
                new Uri("https://ambientcg.com/a/" + id),
                AssetJson.Https(thumbnail, "acg-media.struffelproductions.com", "ambientcg.com"),
                AssetJson.Cc0, VisualAssetDownloadAccess.ProviderPage));
        }
        var total = AssetJson.Number(root, "totalResults");
        // Ignore the untrusted nextPageHttp URL. Reconstruct the same endpoint with a bounded numeric offset.
        var next = total.HasValue ? AssetJson.NextOffset(offset, assets.GetArrayLength(),
            (int)Math.Min(total.Value, int.MaxValue)) : null;
        return new(Info, results.AsReadOnly(), next);
    }
}
