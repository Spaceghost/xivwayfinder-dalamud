using System.Text.Json;

namespace XivVisualAssets;

// Public search: https://sketchfab.com/developers/data-api/v3
// Downloads require the end user's provider authentication: https://sketchfab.com/developers/download-api
internal sealed class SketchfabAssetProvider(AssetMetadataHttp http) : IVisualAssetSearchProvider
{
    public VisualAssetProviderInfo Info { get; } = new("sketchfab", "Sketchfab", "Models from Sketchfab",
        Array.AsReadOnly(new[] { VisualAssetKind.All, VisualAssetKind.Model, VisualAssetKind.Animation }));

    public async Task<VisualAssetSearchPage> SearchAsync(VisualAssetSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        request = request.Validated();
        cancellationToken.ThrowIfCancellationRequested();
        if (!Info.SupportedKinds.Contains(request.Kind)) return AssetJson.Unsupported(Info);
        var cursor = request.PageToken ?? "";
        // Cursor contents only enter a query value, never a request host/path or Authorization header.
        var uri = new Uri("https://api.sketchfab.com/v3/search?type=models&downloadable=true&q=" +
            Uri.EscapeDataString(request.Query) + "&count=" + request.PageSize +
            (request.Kind == VisualAssetKind.Animation ? "&animated=true" : "") +
            (cursor.Length == 0 ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
        using var document = await http.GetAsync(uri, AssetMetadataHttp.PageByteLimit,
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var assets = AssetJson.Array(root, "results");
        if (assets.GetArrayLength() > request.PageSize)
            throw new InvalidDataException("Sketchfab returned too many results.");
        var results = new List<VisualAsset>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in assets.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = AssetJson.Text(item, "uid", 129);
            var title = AssetJson.Text(item, "name");
            var clips = AssetJson.Number(item, "animationCount");
            if (!AssetJson.Id(id) || !ids.Add(id) || string.IsNullOrWhiteSpace(title) ||
                request.Kind == VisualAssetKind.Animation && clips is not > 0) continue;
            var license = AssetJson.Field(item, "license");
            var label = AssetJson.Text(license, "label");
            if (string.IsNullOrWhiteSpace(label)) label = "License not supplied — check provider";
            results.Add(new("sketchfab", id, title,
                AssetJson.Text(AssetJson.Field(item, "user"), "displayName"), VisualAssetKind.Model,
                new Uri("https://sketchfab.com/3d-models/" + id), Thumbnail(item),
                new(label, AssetJson.Https(AssetJson.Text(license, "url", 2048), "creativecommons.org", "sketchfab.com")),
                AssetJson.True(item, "isDownloadable") ? VisualAssetDownloadAccess.ProviderSignIn : VisualAssetDownloadAccess.Unavailable,
                clips.HasValue ? (int)Math.Min(clips.Value, int.MaxValue) : null, AssetJson.Number(item, "faceCount"))
                { Archives = Archives(item) });
        }
        // Never follow the response's arbitrary next URL. A bounded opaque cursor is escaped into our own URL.
        var next = AssetJson.Text(AssetJson.Field(root, "cursors"), "next", 257);
        if (next.Length is 0 or > 256 || next == cursor || assets.GetArrayLength() == 0) next = null;
        return new(Info, results.AsReadOnly(), next,
            "Metadata search only. Downloads require your Sketchfab sign-in; review each model's license. " +
            (request.Kind == VisualAssetKind.Animation ? "Results are models with embedded animation clips, not retargetable motion packs." : ""));
    }

    private static Uri? Thumbnail(JsonElement item)
    {
        var images = AssetJson.Field(AssetJson.Field(item, "thumbnails"), "images");
        if (images.ValueKind != JsonValueKind.Array) return null;
        Uri? best = null;
        long bestWidth = 0;
        foreach (var image in images.EnumerateArray().Take(16))
        {
            var width = AssetJson.Number(image, "width");
            if (width is null or <= 0 or > 512 || width <= bestWidth || AssetJson.Number(image, "height") is not (> 0 and <= 512)) continue;
            var uri = AssetJson.Https(AssetJson.Text(image, "url", 2048), "media.sketchfab.com");
            if (uri is null) continue;
            best = uri;
            bestWidth = width.Value;
        }
        return best;
    }

    private static IReadOnlyList<VisualAssetArchive> Archives(JsonElement item)
    {
        var source = AssetJson.Field(item, "archives");
        if (source.ValueKind != JsonValueKind.Object) return Array.Empty<VisualAssetArchive>();
        var archives = new List<VisualAssetArchive>();
        foreach (var archive in source.EnumerateObject().Take(16))
        {
            var format = AssetJson.Text(archive.Value, "type", 16).ToLowerInvariant();
            if (format.Length == 0 || !format.All(char.IsAsciiLetterOrDigit) ||
                !string.Equals(format, archive.Name, StringComparison.OrdinalIgnoreCase)) continue;
            archives.Add(new(format, AssetJson.Number(archive.Value, "size"),
                AssetJson.Number(archive.Value, "faceCount"), AssetJson.Number(archive.Value, "vertexCount"),
                AssetJson.Number(archive.Value, "textureMaxResolution")));
        }
        return archives.AsReadOnly();
    }
}
