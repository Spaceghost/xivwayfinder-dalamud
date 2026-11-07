namespace XivVisualAssets;

/// <summary>Capabilities are explicit inputs so another visual mod can supply a different import budget.</summary>
public sealed record VisualAssetImportProfile(long MaxTriangles = 20_000, long MaxVertices = 60_000,
    long MaxFileBytes = 32 * 1024 * 1024, long MaxTextureResolution = 4096, bool PreferAnimation = true)
{
    // glTF/GLB 2 accepts embedded or local resources; OBJ supplies static geometry and diffuse MTL appearance.
    public IReadOnlyList<string> Formats { get; init; } = Array.AsReadOnly(new[] { "glb", "gltf", "obj" });
    public IReadOnlyList<string> AnimatedFormats { get; init; } = Array.AsReadOnly(new[] { "glb", "gltf" });
}

public enum VisualAssetFit { NotGuideModel, Unavailable, NeedsOptimization, NeedsConversion, UnknownFormat, MetadataCandidate }

public sealed record VisualAssetCompatibilityAssessment(int Score, VisualAssetFit Fit, string Label,
    IReadOnlyList<string> Badges, IReadOnlyList<string> Reasons)
{
    public const string VerificationNotice = "Metadata estimate only — not downloaded, imported, or verified in FFXIV.";
}

/// <summary>
/// Pure, deterministic suitability ranking. It never fetches files and never claims a remote asset works.
/// Unknown information earns no fabricated credit; a reported budget violation outranks optimistic format hints.
/// </summary>
public static class VisualAssetCompatibility
{
    public static VisualAssetCompatibilityAssessment Assess(VisualAsset asset, VisualAssetImportProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        profile ??= new();
        if (profile.MaxTriangles <= 0 || profile.MaxVertices <= 0 || profile.MaxFileBytes <= 0 || profile.MaxTextureResolution <= 0
            || profile.Formats is null || profile.AnimatedFormats is null)
            throw new ArgumentOutOfRangeException(nameof(profile));
        var badges = new List<string>();
        var reasons = new List<string>();
        VisualAssetCompatibilityAssessment Result(int score, VisualAssetFit fit, string label) =>
            new(score, fit, label, badges.AsReadOnly(), reasons.AsReadOnly());

        if (asset.Kind is not (VisualAssetKind.Model or VisualAssetKind.Animation))
        {
            reasons.Add("This result is a material, texture or environment, not a companion model.");
            return Result(0, VisualAssetFit.NotGuideModel, "Not a guide model");
        }
        if (asset.DownloadAccess == VisualAssetDownloadAccess.Unavailable)
        {
            reasons.Add("The provider does not currently advertise a downloadable model.");
            return Result(0, VisualAssetFit.Unavailable, "Download unavailable");
        }

        var archives = asset.Archives.Take(16).ToArray();
        var supported = archives.Where(a => profile.Formats.Contains(a.Format, StringComparer.OrdinalIgnoreCase)).ToArray();
        bool HasAnimationSupport(VisualAssetArchive archive) => profile.AnimatedFormats.Contains(archive.Format, StringComparer.OrdinalIgnoreCase);
        // Rank each offered usable archive by its own metadata; do not combine a
        // small glTF archive with a large GLB and call the GLB small enough.
        var selected = supported.OrderBy(a => OverBudget(a, asset.FaceCount, profile))
            .ThenByDescending(a => profile.PreferAnimation && asset.AnimationCount > 0 && HasAnimationSupport(a))
            .ThenBy(a => FormatPreference(a.Format))
            .ThenBy(a => a.SizeBytes ?? long.MaxValue).FirstOrDefault();
        var score = selected is null ? 12 : FormatPreference(selected.Format) switch { 0 => 45, 1 => 42, _ => 30 };
        var fit = selected is null ? archives.Length == 0 ? VisualAssetFit.UnknownFormat : VisualAssetFit.NeedsConversion : VisualAssetFit.MetadataCandidate;
        if (selected is not null)
        {
            badges.Add(selected.Format.ToUpperInvariant() + " advertised");
            if (selected.Format.Equals("gltf", StringComparison.OrdinalIgnoreCase))
            {
                badges.Add("Unpack model folder");
                reasons.Add("Unpack the glTF archive and select its .gltf file with its local buffers and PNG/JPEG textures together in the model folder.");
                reasons.Add($"The extracted model, buffers and textures must total at most {profile.MaxFileBytes:N0} bytes; compressed archive size does not establish that total.");
            }
        }
        else reasons.Add(archives.Length == 0 ? "The search response does not identify a supported file format." :
            "No advertised archive uses a format supported by the current importer.");

        if (asset.AnimationCount is > 0)
        {
            badges.Add($"{asset.AnimationCount.Value} clip(s) advertised");
            if (profile.PreferAnimation && selected is not null && HasAnimationSupport(selected)) score += 18;
            if (selected?.Format.Equals("obj", StringComparison.OrdinalIgnoreCase) == true)
                reasons.Add("OBJ does not carry the provider's animated clips; choose or export a GLB or glTF for animation.");
            else if (selected is not null && !HasAnimationSupport(selected))
                reasons.Add("This import profile does not advertise animation support for the selected format.");
        }
        else if (asset.AnimationCount == 0) badges.Add("No clips advertised");
        else badges.Add("Animation unknown");
        if (asset.Rigged == true) { badges.Add("Rig reported"); score += 4; }
        else if (asset.Rigged is null) reasons.Add("Skeleton/rig compatibility is unknown; animation clips alone do not establish it.");

        var faces = selected?.FaceCount ?? asset.FaceCount;
        var tooLarge = false;
        void Budget(long? value, long max, string noun, int reward)
        {
            if (value is null or < 0) return;
            if (value > max)
            {
                tooLarge = true;
                reasons.Add($"Reported {value.Value:N0} {noun} exceeds this importer's {max:N0} budget.");
            }
            else score += reward;
        }
        Budget(faces, profile.MaxTriangles, "faces (triangle budget)", 8);
        Budget(selected?.VertexCount, profile.MaxVertices, "vertices", 5);
        Budget(selected?.SizeBytes, profile.MaxFileBytes, "archive bytes", 6);
        Budget(selected?.TextureMaxResolution, profile.MaxTextureResolution, "texture pixels per side", 3);
        if (faces is null) reasons.Add("Geometry size is unknown; polygon counts may also grow when triangulated.");
        if (selected is not null)
            reasons.Add("Model resources, compression/extensions, texture totals, skinning and animation behavior still require local import validation.");
        badges.Add(asset.DownloadAccess == VisualAssetDownloadAccess.ProviderSignIn ? "Provider sign-in required" : "Download on provider");
        reasons.Add("Review the displayed license and attribution requirements before using or sharing this asset.");
        if (tooLarge)
        {
            fit = VisualAssetFit.NeedsOptimization;
            score = Math.Min(score, 8);
            badges.Add("Over import budget");
        }
        var label = fit switch
        {
            VisualAssetFit.NeedsOptimization => "Needs optimization",
            VisualAssetFit.NeedsConversion => "Needs format conversion",
            VisualAssetFit.UnknownFormat => "Format unverified",
            _ => "Promising metadata fit",
        };
        return Result(Math.Clamp(score, 0, 100), fit, label);
    }

    /// <summary>Orders a bounded set already loaded by the UI; does not fetch more pages to chase a ranking.</summary>
    public static IReadOnlyList<VisualAsset> Rank(IEnumerable<VisualAsset> assets, VisualAssetImportProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var loaded = assets.Take(1001).ToArray();
        if (loaded.Length > 1000) throw new ArgumentException("Rank at most 1,000 loaded results at a time.", nameof(assets));
        return Array.AsReadOnly(loaded.Select(a => (Asset: a, Fit: Assess(a, profile)))
            .OrderByDescending(a => a.Fit.Score).ThenBy(a => a.Asset.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Asset.ProviderId, StringComparer.Ordinal).ThenBy(a => a.Asset.Id, StringComparer.Ordinal)
            .Select(a => a.Asset).ToArray());
    }

    private static bool OverBudget(VisualAssetArchive a, long? fallbackFaces, VisualAssetImportProfile p) =>
        (a.FaceCount ?? fallbackFaces) > p.MaxTriangles || a.VertexCount > p.MaxVertices ||
        a.SizeBytes > p.MaxFileBytes || a.TextureMaxResolution > p.MaxTextureResolution;

    private static int FormatPreference(string format) => format.Equals("glb", StringComparison.OrdinalIgnoreCase) ? 0
        : format.Equals("gltf", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
}
