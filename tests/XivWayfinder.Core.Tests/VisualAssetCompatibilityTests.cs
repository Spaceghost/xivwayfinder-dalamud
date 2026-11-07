using XivVisualAssets;
using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public class VisualAssetCompatibilityTests
{
    private static VisualAsset Model(string id = "fox", int? clips = null) => new("test", id, id, "Artist",
        VisualAssetKind.Model, new("https://example.com/model"), null, new("CC Attribution", null),
        VisualAssetDownloadAccess.ProviderSignIn, clips);
    private static VisualAssetArchive Glb(long faces = 1000, long bytes = 100_000) => new("glb", bytes, faces, faces * 2, 1024);

    [Fact]
    public void DefaultProfileTracksActualReaderGeometryLimit()
    {
        Assert.Equal(GuideMesh.MaxTriangles, new VisualAssetImportProfile().MaxTriangles);
        Assert.Equal(GuideMesh.MaxTriangles * 3, new VisualAssetImportProfile().MaxVertices);
        Assert.Equal(new[] { "glb", "gltf", "obj" }, new VisualAssetImportProfile().Formats);
        Assert.Equal(new[] { "glb", "gltf" }, new VisualAssetImportProfile().AnimatedFormats);
    }

    [Fact]
    public void AnimatedGlbMetadataRanksAboveStaticObjAndUnknownButNeverClaimsItWorks()
    {
        var animated = Model("animated", 3) with { Archives = [Glb()] };
        var staticModel = Model("static", 0) with { Archives = [new("obj", 10_000, 1000)] };
        var unknown = Model("unknown");
        Assert.Equal(new[] { animated, staticModel, unknown }, VisualAssetCompatibility.Rank([unknown, staticModel, animated]));
        var assessment = VisualAssetCompatibility.Assess(animated);
        Assert.Equal(VisualAssetFit.MetadataCandidate, assessment.Fit);
        Assert.Equal("Promising metadata fit", assessment.Label);
        Assert.Contains("GLB advertised", assessment.Badges);
        Assert.Contains("Provider sign-in required", assessment.Badges);
        Assert.Contains(assessment.Reasons, r => r.Contains("still require local import validation"));
        Assert.Contains("not downloaded", VisualAssetCompatibilityAssessment.VerificationNotice);
        Assert.Contains("verified in FFXIV", VisualAssetCompatibilityAssessment.VerificationNotice);
    }

    [Fact]
    public void GltfArchiveIsASeparateSupportedFormatWithHonestPackagingRequirements()
    {
        var asset = Model(clips: 5) with { Archives = [new("gltf", 100_000, 1000)] };
        var fit = VisualAssetCompatibility.Assess(asset);
        Assert.Equal(VisualAssetFit.MetadataCandidate, fit.Fit);
        Assert.Contains("GLTF advertised", fit.Badges);
        Assert.Contains("Unpack model folder", fit.Badges);
        Assert.Contains(fit.Reasons, r => r.Contains("compressed archive size does not establish"));
        Assert.Contains(fit.Reasons, r => r.Contains("local buffers and PNG/JPEG textures"));
        Assert.DoesNotContain("GLB advertised", fit.Badges);
    }

    [Fact]
    public void AnimationMetadataDoesNotInventSkeletonOrObjAnimationSupport()
    {
        var asset = Model(clips: 3) with { Archives = [new("obj", 100_000, 1000)] };
        var fit = VisualAssetCompatibility.Assess(asset);
        Assert.DoesNotContain("Rig reported", fit.Badges);
        Assert.Contains(fit.Reasons, r => r.StartsWith("Skeleton/rig compatibility is unknown"));
        Assert.Contains(fit.Reasons, r => r.StartsWith("OBJ does not carry"));
    }

    [Theory]
    [InlineData(20_001, 10_000, 10_000, 1024)]
    [InlineData(1000, 33_554_433, 10_000, 1024)]
    [InlineData(1000, 10_000, 60_001, 1024)]
    [InlineData(1000, 10_000, 10_000, 8192)]
    public void ReportedBudgetViolationsOutweighAttractiveFormatAndAnimations(long faces, long bytes, long vertices, long resolution)
    {
        var asset = Model(clips: 20) with { Archives = [new("glb", bytes, faces, vertices, resolution)], Rigged = true };
        var fit = VisualAssetCompatibility.Assess(asset);
        Assert.Equal(VisualAssetFit.NeedsOptimization, fit.Fit);
        Assert.InRange(fit.Score, 0, 8);
        Assert.Contains("Over import budget", fit.Badges);
        Assert.Contains(fit.Reasons, r => r.Contains("exceeds this importer's"));
    }

    [Fact]
    public void SelectsUsableGltfWithoutCallingOverBudgetGlbUsable()
    {
        var asset = Model(clips: 2) with { Archives = [Glb(bytes: 40_000_000), new("gltf", 1000, 1000)] };
        var fit = VisualAssetCompatibility.Assess(asset);
        Assert.Equal(VisualAssetFit.MetadataCandidate, fit.Fit);
        Assert.Contains("GLTF advertised", fit.Badges);
        Assert.DoesNotContain("GLB advertised", fit.Badges);
    }

    [Fact]
    public void CannotCombineSmallArchiveSizeWithAnotherFormatsSmallGeometry()
    {
        var asset = Model(clips: 2) with { Archives = [Glb(bytes: 40_000_000), new("gltf", 1000, 30_000)] };
        Assert.Equal(VisualAssetFit.NeedsOptimization, VisualAssetCompatibility.Assess(asset).Fit);
    }

    [Fact]
    public void CustomFormatsCanExcludeGltfWithoutBorrowingItsBudget()
    {
        var profile = new VisualAssetImportProfile { Formats = ["glb", "obj"] };
        var asset = Model(clips: 2) with { Archives = [Glb(bytes: 40_000_000), new("gltf", 1000, 1000)] };
        Assert.Equal(VisualAssetFit.NeedsOptimization, VisualAssetCompatibility.Assess(asset, profile).Fit);
        var gltfOnly = Model(clips: 2) with { Archives = [new("gltf", 1000, 1000)] };
        Assert.Equal(VisualAssetFit.NeedsConversion, VisualAssetCompatibility.Assess(gltfOnly, profile).Fit);
    }

    [Fact]
    public void AnimatedGltfGetsClipCreditAndRanksAheadOfStaticObj()
    {
        var animated = Model("gltf", 3) with { Archives = [new("gltf", 1000, 1000)] };
        var staticModel = Model("obj", 0) with { Archives = [new("obj", 1000, 1000)] };
        Assert.Equal(animated, VisualAssetCompatibility.Rank([staticModel, animated])[0]);
        Assert.Equal(18, VisualAssetCompatibility.Assess(animated).Score - VisualAssetCompatibility.Assess(animated, new(PreferAnimation: false)).Score);
    }

    [Fact]
    public void OtherImportersCanDeclareTheirOwnAnimationFormats()
    {
        var asset = Model(clips: 2) with { Archives = [new("obj", 1000, 1000), new("fbx", 1000, 1000)] };
        var profile = new VisualAssetImportProfile { Formats = ["obj", "fbx"], AnimatedFormats = ["fbx"] };
        Assert.Contains("FBX advertised", VisualAssetCompatibility.Assess(asset, profile).Badges);
        var staticOnly = profile with { AnimatedFormats = [] };
        Assert.Equal(18, VisualAssetCompatibility.Assess(asset, profile).Score - VisualAssetCompatibility.Assess(asset, staticOnly).Score);
    }

    [Fact]
    public void PrefersUsableStaticObjOverKnownOverBudgetGlb()
    {
        var asset = Model(clips: 2) with { Archives = [Glb(bytes: 40_000_000), new("obj", 1000, 1000)] };
        var fit = VisualAssetCompatibility.Assess(asset);
        Assert.Equal(VisualAssetFit.MetadataCandidate, fit.Fit);
        Assert.Contains("OBJ advertised", fit.Badges);
    }

    [Fact]
    public void UnknownFormatsSizesAndAnimationStayExplicitlyUnknown()
    {
        var fit = VisualAssetCompatibility.Assess(Model());
        Assert.Equal(VisualAssetFit.UnknownFormat, fit.Fit);
        Assert.Contains("Animation unknown", fit.Badges);
        Assert.Contains(fit.Reasons, r => r.StartsWith("Geometry size is unknown"));
    }

    [Fact]
    public void NonModelsAndUnavailableDownloadsDoNotRankAsUsableGuides()
    {
        Assert.Equal(VisualAssetFit.NotGuideModel, VisualAssetCompatibility.Assess(Model() with { Kind = VisualAssetKind.Texture }).Fit);
        Assert.Equal(VisualAssetFit.Unavailable, VisualAssetCompatibility.Assess(Model() with { DownloadAccess = VisualAssetDownloadAccess.Unavailable }).Fit);
    }

    [Fact]
    public void OtherVisualModsCanSupplyDifferentCapabilitiesWithoutGameTypes()
    {
        var asset = Model() with { Archives = [Glb(faces: 25_000)] };
        Assert.Equal(VisualAssetFit.NeedsOptimization, VisualAssetCompatibility.Assess(asset).Fit);
        Assert.Equal(VisualAssetFit.MetadataCandidate, VisualAssetCompatibility.Assess(asset, new(MaxTriangles: 30_000)).Fit);
    }

    [Fact]
    public void RankingHasDeterministicTiesAndNoSilentCapacityTruncation()
    {
        Assert.Equal("a", VisualAssetCompatibility.Rank([Model("b"), Model("a")])[0].Id);
        Assert.Throws<ArgumentException>(() => VisualAssetCompatibility.Rank(Enumerable.Repeat(Model(), 1001)));
    }
}
