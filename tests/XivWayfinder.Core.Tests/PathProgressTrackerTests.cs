using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class PathProgressTrackerTests
{
    private static readonly Vector3[] Bend = [new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new(20, 0, 10)];

    [Fact]
    public void LookaheadMovesContinuouslyInsteadOfDemandingEachCorner()
    {
        var state = new PathProgressTracker();
        var before = state.Update(Bend, new(9, 0, .5f));
        var after = state.Update(Bend, new(9.2f, 0, .6f));
        var a = PathTrail.LookAhead(Bend, before.Arc, 4);
        var b = PathTrail.LookAhead(Bend, after.Arc, 4);
        Assert.Equal(9, before.Arc, 4); Assert.Equal(9.2f, after.Arc, 4);
        Assert.Equal(new Vector3(10, 0, 3), a);
        Assert.InRange(Vector3.Distance(a, b), .19f, .21f);
    }

    [Fact]
    public void FirstProjectionAtCrossingChoosesEarliestLegAndDoesNotJumpToLaterLeg()
    {
        Vector3[] path = [new(-5, 0, 0), new(5, 0, 0), new(5, 0, 5), new(0, 0, 5), new(0, 0, -5)];
        var state = new PathProgressTracker();
        Assert.Equal(5, state.Update(path, Vector3.Zero).Arc);
        var next = state.Update(path, new(.1f, 0, -.1f));
        Assert.InRange(next.Arc, 5, 5.3f);
        for (var i = 0; i < 500; i++) Assert.Equal(next.Arc, state.Update(path, new(.1f, 0, -.1f)).Arc);
    }

    [Fact]
    public void ParallelReturnLegDoesNotPullGuideBackwardOrFarForward()
    {
        Vector3[] path = [new(0, 0, 0), new(20, 0, 0), new(20, 0, 1), new(0, 0, 1)];
        var state = new PathProgressTracker(); state.Update(path, new(7, 0, .4f));
        var next = state.Update(path, new(7.2f, 0, .6f));
        Assert.InRange(next.Arc, 7.1f, 7.3f); Assert.Equal(1, next.NextCorner);
    }

    [Fact]
    public void TinyBackwardJitterIsIgnoredButIntentionalBacktrackingWorks()
    {
        var state = new PathProgressTracker(); state.Update(Bend, new(5, 0, 0));
        Assert.Equal(5, state.Update(Bend, new(4.95f, 0, 0)).Arc);
        Assert.Equal(5, state.Update(Bend, new(4.9f, 0, 0)).Arc);
        Assert.InRange(state.Update(Bend, new(4.7f, 0, 0)).Arc, 4.69f, 4.71f);
        Assert.InRange(state.Update(Bend, new(4, 0, 0)).Arc, 3.99f, 4.01f);
    }

    [Fact]
    public void OwnWalkableCornerCutAdvancesThroughTheSameRouteWithoutWaypointSnap()
    {
        var state = new PathProgressTracker(); state.Update(Bend, new(8, 0, 0));
        var prior = 8f;
        foreach (var p in new Vector3[] { new(9, 0, .5f), new(9.5f, 0, 1.5f), new(10, 0, 2.5f) })
        {
            var frame = state.Update(Bend, p);
            Assert.True(frame.Arc >= prior); Assert.True(frame.Arc - prior < 3);
            Assert.True(frame.Away < 2); prior = frame.Arc;
            var ahead = PathTrail.LookAhead(Bend, frame.Arc, 4);
            Assert.Equal(10, ahead.X); Assert.True(ahead.Z > p.Z);
        }
    }

    [Fact]
    public void VerticalSeparationPenalizesWrongFloorAndDepartureIsVisibleForRequery()
    {
        Vector3[] path = [new(0, 0, 0), new(10, 0, 0), new(10, 8, 10), new(0, 8, 0)];
        var state = new PathProgressTracker();
        Assert.Equal(5, state.Update(path, new(5, 0, 0)).Arc);
        var away = state.Update(path, new(5, 7, 0));
        Assert.True(away.Away > 4);
        Assert.True(away.Arc < 10);
    }

    [Fact]
    public void DiscontinuityRequestsFreshRouteUntilResetInsteadOfSnappingGuide()
    {
        var state = new PathProgressTracker(); var start = state.Update(Bend, new(1, 0, 0));
        var moved = state.Update(Bend, new(19, 0, 10));
        Assert.True(moved.Relocated); Assert.Equal(start.Arc, moved.Arc);
        Assert.True(state.Update(Bend, new(19, 0, 10)).Relocated);
        state.Reset();
        Assert.False(state.Update([new(19, 0, 10), new(20, 0, 10)], new(19, 0, 10)).Relocated);
    }

    [Fact]
    public void DuplicatePointsInvalidCoordinatesAndShortPathsStaySafe()
    {
        var state = new PathProgressTracker();
        Assert.False(state.Update([], Vector3.Zero).Valid);
        Assert.False(state.Update([Vector3.Zero], Vector3.Zero).Valid);
        Assert.False(state.Update([Vector3.Zero, Vector3.Zero], Vector3.Zero).Valid);
        Assert.False(state.Update(Bend, new(float.NaN, 0, 0)).Valid);
        Assert.False(state.Update([Vector3.Zero, new(float.PositiveInfinity, 0, 0)], Vector3.Zero).Valid);
        Assert.Equal(3, state.Update([Vector3.Zero, Vector3.Zero, new(10, 0, 0)], new(3, 0, 0)).Arc);
    }

    [Fact]
    public void ProgressAndLookaheadClampAtDestinationAndResetForNewRoute()
    {
        var state = new PathProgressTracker();
        var end = state.Update(Bend, new(22, 0, 10));
        Assert.Equal(30, end.Arc); Assert.Equal(3, end.NextCorner);
        Assert.Equal(Bend[^1], PathTrail.LookAhead(Bend, end.Arc, 4));
        state.Reset();
        Assert.Equal(0, state.Update(Bend, Vector3.Zero).Arc);
    }

    [Fact]
    public void LeadTrailAndRugExportUseIdenticalProgressOnAmbiguousParallelLegs()
    {
        Vector3[] path = [new(0, 0, 0), new(20, 0, 0), new(20, 0, 1), new(0, 0, 1)];
        var state = new PathProgressTracker(); state.Update(path, new(7, 0, .4f));
        var player = new Vector3(7.2f, 0, .6f); var progress = state.Update(path, player);
        Assert.True(PathTrail.Project(path, player, out _) > 30); // Stateless nearest would switch branches.
        var lead = MinionGlove.Lead(player, path, Vector2.UnitX, 4, MinionLayout.Default, 0,
            progressArc: progress.Arc)!.Value;
        var visual = RouteVisual.Create(path, player, 4, 8, Colors.Gold, progress.Arc)!;
        Assert.Equal(visual.GuidePosition.X, lead.At.X, 4);
        Assert.Equal(visual.GuidePosition.Z, lead.At.Z, 4);
        Span<TrailPoint> trail = stackalloc TrailPoint[4];
        var count = PathTrail.Sample(path, progress.Arc, 1, 0, 4, trail);
        Assert.True(count > 0);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(0, trail[i].Position.Z);
            Assert.Equal(progress.Arc + trail[i].Ahead, trail[i].Position.X, 4);
        }
    }

    [Fact]
    public void SteadyTrackingHasNoPerFrameManagedAllocations()
    {
        var state = new PathProgressTracker(); state.Update(Bend, new(5, 0, 0));
        for (var i = 0; i < 100; i++) state.Update(Bend, new(5, 0, 0));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) state.Update(Bend, new(5, 0, 0));
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
