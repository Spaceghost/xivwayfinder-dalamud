namespace XivVisualAssets;

/// <summary>Animation searches find models with embedded clips, not retargetable motion packs.</summary>
public enum VisualAssetKind { All, Model, Material, Texture, Animation, Environment }

/// <summary>No result grants permission to download or import a remote file automatically.</summary>
public enum VisualAssetDownloadAccess { ProviderPage, ProviderSignIn, Unavailable }

public sealed record VisualAssetLicense(string Name, Uri? Details);

/// <summary>Provider-reported archive metadata, not proof that a file has been downloaded or validated.</summary>
public sealed record VisualAssetArchive(string Format, long? SizeBytes = null, long? FaceCount = null,
    long? VertexCount = null, long? TextureMaxResolution = null);

public sealed record VisualAsset(
    string ProviderId, string Id, string Title, string Author, VisualAssetKind Kind,
    Uri Source, Uri? Thumbnail, VisualAssetLicense License,
    VisualAssetDownloadAccess DownloadAccess, int? AnimationCount = null, long? FaceCount = null)
{
    public IReadOnlyList<VisualAssetArchive> Archives { get; init; } = Array.Empty<VisualAssetArchive>();
    // Unknown is deliberately distinct from false: clips alone do not establish a skeleton.
    public bool? Rigged { get; init; }
}

public sealed record VisualAssetProviderInfo(
    string Id, string Name, string Credit, IReadOnlyList<VisualAssetKind> SupportedKinds);

public sealed record VisualAssetSearchRequest(
    string Query, VisualAssetKind Kind = VisualAssetKind.Model, int PageSize = 20, string? PageToken = null)
{
    public const int MaxQueryLength = 160;
    public const int MaxPageSize = 50;
    internal const int MaxOffset = 10_000;

    internal VisualAssetSearchRequest Validated()
    {
        if (Query is null || Query.Length > MaxQueryLength)
            throw new ArgumentException($"Search text must be at most {MaxQueryLength} characters.", nameof(Query));
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
        if (PageSize is < 1 or > MaxPageSize) throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (PageToken is { Length: > 256 } || PageToken?.Any(char.IsControl) == true)
            throw new ArgumentException("Invalid page token.", nameof(PageToken));
        return this with { Query = AssetJson.Clean(Query, MaxQueryLength).Trim() };
    }

    internal int Offset()
    {
        if (string.IsNullOrEmpty(PageToken)) return 0;
        if (!int.TryParse(PageToken, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value is < 0 or > MaxOffset)
            throw new ArgumentException("Invalid page offset.", nameof(PageToken));
        return value;
    }
}

public sealed record VisualAssetSearchPage(
    VisualAssetProviderInfo Provider, IReadOnlyList<VisualAsset> Items, string? NextPageToken,
    string? Notice = null);

public interface IVisualAssetSearchProvider
{
    VisualAssetProviderInfo Info { get; }
    Task<VisualAssetSearchPage> SearchAsync(VisualAssetSearchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Metadata-only repository searches. No Dalamud, Ghostty, game-server, file import or download dependency.
/// Constructing this service makes no requests. The caller owns search scheduling/cancellation and UI publication.
/// </summary>
public sealed class VisualAssetSearch : IDisposable
{
    private readonly AssetMetadataHttp http;
    private readonly Dictionary<string, IVisualAssetSearchProvider> providers;
    public IReadOnlyList<VisualAssetProviderInfo> Providers { get; }

    public VisualAssetSearch() : this(new AssetMetadataHttp()) { }

    // An injected handler makes provider tests entirely offline. Production redirects are disabled below.
    public VisualAssetSearch(HttpMessageHandler handler) : this(new AssetMetadataHttp(handler)) { }

    private VisualAssetSearch(AssetMetadataHttp http)
    {
        this.http = http;
        IVisualAssetSearchProvider[] sources =
        [new PolyHavenAssetProvider(http), new AmbientCgAssetProvider(http), new SketchfabAssetProvider(http)];
        providers = sources.ToDictionary(p => p.Info.Id, StringComparer.Ordinal);
        Providers = Array.AsReadOnly(sources.Select(p => p.Info).ToArray());
    }

    public Task<VisualAssetSearchPage> SearchAsync(string providerId, VisualAssetSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!providers.TryGetValue(providerId, out var provider))
            throw new ArgumentException("Unknown asset provider.", nameof(providerId));
        return provider.SearchAsync(request, cancellationToken);
    }

    public void Dispose() => http.Dispose();
}
