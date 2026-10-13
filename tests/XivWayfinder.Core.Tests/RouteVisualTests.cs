using System.Numerics;
using System.Text.Json;
using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public class RouteVisualTests
{
    private static readonly Vector3[] Path = [new(0, 0, 0), new(10, 2, 0), new(10, 4, 10)];

    [Fact]
    public void ExportMatchesGuideAndLookAheadOnTheSameBentPath()
    {
        var visual = RouteVisual.Create(Path, new(6, 1.2f, 0), 5.5f, 8, Colors.Flag)!;
        Assert.Equal(6, visual.Progress);
        Assert.Equal(11.5f, visual.GuideArc); // configured 4 + MSQ eager 1.5
        Assert.Equal(14, visual.LookAheadArc); // existing guide look-at remains configured 4 + 4
        Assert.InRange(Vector3.Distance(new Vector3(10, 2.3f, 1.5f), visual.GuidePosition), 0, .00001f);
        Assert.InRange(Vector3.Distance(new Vector3(10, 2.8f, 4), visual.LookAheadPosition), 0, .00001f);
        Assert.Equal(Colors.Flag, visual.Color);
    }

    [Fact]
    public void AuthoritativeProgressOverridesNearestSegmentForSelfCrossings()
    {
        var visual = RouteVisual.Create(Path, new(10, 4, 9), 4, 8, Colors.Quest, progressArc: 3)!;
        Assert.Equal(3, visual.Progress);
        Assert.Equal(PathTrail.At(Path, 7), visual.GuidePosition);
        Assert.Equal(PathTrail.At(Path, 11), visual.LookAheadPosition);
    }

    [Fact]
    public void NearDestinationPositionsClampToPathEnd()
    {
        var visual = RouteVisual.Create(Path, Path[^1], 5.5f, 8, Colors.MainScenario)!;
        Assert.Equal(20, visual.Progress);
        Assert.Equal(20, visual.GuideArc);
        Assert.Equal(20, visual.LookAheadArc);
        Assert.Equal(Path[^1], visual.GuidePosition);
    }

    [Fact]
    public void NonLeadingGuideUsesCurrentProgressButKeepsNextLookAhead()
    {
        var visual = RouteVisual.Create(Path, new(6, 0, 0), 0, 4, Colors.Gold)!;
        Assert.Equal(visual.Progress, visual.GuideArc);
        Assert.Equal(10, visual.LookAheadArc);
    }

    [Fact]
    public void IndependentCompanionCanReturnBehindPlayerWithoutInventingAShortcut()
    {
        var visual = RouteVisual.FromArcs(Path, 16, 8, 10, Colors.Flag)!;
        Assert.Equal(16, visual.Progress);
        Assert.Equal(8, visual.GuideArc);
        Assert.Equal(PathTrail.At(Path, 8), visual.GuidePosition);
        Assert.Equal(PathTrail.At(Path, 10), visual.LookAheadPosition);
        Assert.Null(RouteVisual.FromArcs([Vector3.Zero, new(100, 0, 0)], 90, 1, 4, Colors.Flag));
        Assert.Null(RouteVisual.FromArcs(Path, 16, -1, 10, Colors.Flag));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(21)]
    public void InvalidSharedProgressIsNotReplacedWithAnUnrelatedNearestPoint(float progress) =>
        Assert.Null(RouteVisual.Create(Path, Vector3.Zero, 4, 8, Colors.Gold, progress));

    [Fact]
    public void InvalidCoordinatesDistancesAndColorsAreRejected()
    {
        Assert.Null(RouteVisual.Create([Vector3.Zero, new(float.NaN)], Vector3.Zero, 4, 8, Colors.Gold));
        Assert.Null(RouteVisual.Create(Path, new(float.NaN), 4, 8, Colors.Gold));
        Assert.Null(RouteVisual.Create(Path, Vector3.Zero, float.NaN, 8, Colors.Gold));
        Assert.Null(RouteVisual.Create(Path, Vector3.Zero, 4, 41, Colors.Gold));
        Assert.Null(RouteVisual.Create(Path, Vector3.Zero, 4, 8, new(float.NaN)));
        Assert.Equal(new Vector4(0, 1, .5f, 1), RouteVisual.Create(Path, Vector3.Zero, 4, 8, new(-1, 2, .5f, 1))!.Color);
    }

    [Fact]
    public void SnapshotAddsOptionalVisualMetadataWithoutBreakingVersionOne()
    {
        var state = new WayfinderState
        {
            Enabled = true,
            Visible = true,
            Navmesh = true,
            PlayerTerritoryId = 10,
            Guide = new(GuideKind.Walk, new(TargetSource.Quest, 10, 10, 10, 4, "Task"), 20, Vector2.UnitX, null)
        };
        var visual = RouteVisual.Create(Path, new(6, 0, 0), 5.5f, 8, Colors.Quest)!;
        using var doc = JsonDocument.Parse(RouteSnapshot.Encode(state, Path, 1000, visual));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
        var value = doc.RootElement.GetProperty("visual");
        Assert.Equal(6, value.GetProperty("progress").GetSingle());
        Assert.Equal(4, value.GetProperty("color").GetArrayLength());
        Assert.Equal(Colors.Quest.X, value.GetProperty("color")[0].GetSingle());
        Assert.Equal(4, value.GetProperty("lookAhead")[2].GetSingle());
        using var hidden = JsonDocument.Parse(RouteSnapshot.Encode(state with { Visible = false }, Path, 1000, visual));
        Assert.False(hidden.RootElement.TryGetProperty("visual", out _));
    }
}
