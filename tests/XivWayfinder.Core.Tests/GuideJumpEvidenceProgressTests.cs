using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuideJumpEvidenceProgressTests
{
    private static readonly GuideJumpCapability Capability = new(GuideJumpAnimationKind.ImportedClip, 0, "Jump", .8f, .12f, .5f);
    private static readonly CompanionIntentFrame Lead = new(CompanionIntentMode.Lead, 4, 1, true);
    private static readonly Vector3[] Path = [Vector3.Zero, new(10, 0, 0)];

    [Fact]
    public void UnavailableOrOrdinarySlopingGroundNeverArbitrarilyHoldsWalking()
    {
        var progress = new GuideJumpEvidenceProgress();
        Assert.Equal(GuideJumpEvidence.Unknown, progress.Incomplete);
        Assert.True(GuideJumpPolicy.TryRequest(Path, 2, 1, Capability, out var request));
        var floor = new GuideJumpFloor(GuideJumpEvidence.Clear, request!.Start, 1);
        Assert.False(progress.ObserveForwardBlocker(request, floor, request.Point(8) + Vector3.UnitY * .08f, Vector3.UnitY));
        Assert.False(progress.ObserveForwardBlocker(request, floor with { Evidence = GuideJumpEvidence.Unknown },
            request.Point(8) + Vector3.UnitY * .08f, Vector3.UnitX));
        Assert.False(progress.ObserveForwardBlocker(request, floor, new(float.NaN), Vector3.UnitX));
        Assert.Equal(GuideJumpEvidence.Unknown, progress.Incomplete);
        var motion = new CompanionRouteMotion(); motion.Update(Path, 2, Lead, 0, true); motion.ResolvePlacement(true);
        var traversal = new CompanionJumpTraversal((_, _, _, _, _, _) => new(progress.Incomplete, null, "Unavailable"), (_, _) => GuideJumpEvidence.Unknown);
        for (var i = 1; i < 40; ++i)
        { traversal.Update(motion, 1, Path, 2, Lead, Capability, i / 60d, true); traversal.ResolvePlacement(motion, true); }
        Assert.True(motion.Current.Arc > 2.1f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    public void PositiveObstacleKeepsTheSameOwnedArcWhileBoundedFloorEvidenceAccumulates(int floorsPerUpdate)
    {
        var progress = new GuideJumpEvidenceProgress();
        var motion = new CompanionRouteMotion(); motion.Update(Path, 2, Lead, 0, true); motion.ResolvePlacement(true);
        Assert.True(GuideJumpPolicy.TryRequest(Path, 2, 1, Capability, out var request));
        var floors = Enumerable.Range(0, GuideJumpRequest.FloorSampleCount).Select(i => new GuideJumpFloor(
            GuideJumpEvidence.Clear, request!.Point(i) + (i is >= 6 and <= 10 ? Vector3.UnitY * .2f : Vector3.Zero), 1)).ToArray();
        var plan = GuideJumpPolicy.Evaluate(request!, floors).Plan!;
        var collected = 0; var calls = 0;
        var traversal = new CompanionJumpTraversal((_, route, arc, _, _, _) =>
        {
            calls++;
            Assert.Same(Path, route); Assert.Equal(2, arc);
            Assert.True(progress.ObserveForwardBlocker(request!, floors[0], request!.Point(6) + Vector3.UnitY * .08f, -Vector3.UnitX));
            collected += floorsPerUpdate;
            return collected >= floors.Length ? new(GuideJumpEvidence.Approved, plan, "Done")
                : new(progress.Incomplete, null, "Collecting positively observed obstacle");
        }, (_, _) => GuideJumpEvidence.Clear);
        for (var i = 1; !traversal.Active && i < 40; ++i)
        {
            traversal.Update(motion, 1, Path, 2, Lead, Capability, i / 60d, true);
            traversal.ResolvePlacement(motion, true);
            Assert.Equal(2, motion.Current.Arc);
        }
        Assert.True(traversal.Active); Assert.Equal((int)Math.Ceiling(floors.Length / (double)floorsPerUpdate), calls);
        progress.Reset(); Assert.Equal(GuideJumpEvidence.Unknown, progress.Incomplete);
    }

    [Fact]
    public void ExpiredCollectionStopsPendingRatherThanRestartingForeverAtTheSameArc()
    {
        var progress = new GuideJumpEvidenceProgress();
        Assert.True(GuideJumpPolicy.TryRequest(Path, 2, 1, Capability, out var request));
        var floor = new GuideJumpFloor(GuideJumpEvidence.Clear, request!.Start, 1);
        var point = request.Point(6) + Vector3.UnitY * .08f;
        Assert.True(progress.ObserveForwardBlocker(request, floor, point, -Vector3.UnitX));
        Assert.Equal(GuideJumpEvidence.Pending, progress.Incomplete);
        progress.Stop();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Assert.False(progress.ObserveForwardBlocker(request, floor, point, -Vector3.UnitX));
            Assert.Equal(GuideJumpEvidence.Unknown, progress.Incomplete);
        }
        progress.Reset();
        Assert.True(progress.ObserveForwardBlocker(request, floor, point, -Vector3.UnitX));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void WorstBodyCollectionFitsAtThirtyFpsWithFreshCriticalProofAndBoundedQueries(int scenes)
    {
        var progress = new GuideJumpEvidenceProgress(); progress.Begin(0);
        var bulk = GuideJumpRequest.FloorSampleCount + 1
            + GuideJumpEvidenceProgress.TrajectorySegments * GuideJumpEvidenceProgress.MaximumBodySlices;
        var critical = GuideJumpEvidenceProgress.FootprintSamples * 2 + GuideJumpEvidenceProgress.MaximumBodySlices;
        var criticalStarted = double.NaN; var frames = 0;
        while (bulk + critical > 0 && frames < 100)
        {
            var now = ++frames / 30d;
            Assert.False(progress.Expired(now));
            var used = 0;
            while (used + scenes <= GuideJumpEvidenceProgress.SceneQueryBudget && bulk + critical > 0)
            {
                used += scenes;
                if (bulk > 0) bulk--;
                else { if (double.IsNaN(criticalStarted)) criticalStarted = now; critical--; }
                progress.MadeProgress(now);
            }
            Assert.InRange(used, 0, GuideJumpEvidenceProgress.SceneQueryBudget);
        }
        Assert.Equal(0, bulk + critical);
        Assert.True(frames <= 19);
        if (scenes == 4) Assert.True(frames / 30d > .5); // This failed under the old total-age rule.
        Assert.True(GuideJumpEvidenceProgress.Fresh(criticalStarted, frames / 30d));
        Assert.True((GuideJumpEvidenceProgress.MaximumBodySlices + 1) * scenes <= GuideJumpEvidenceProgress.SceneQueryBudget);
    }

    [Fact]
    public void MakingProgressDoesNotExtendTheAbsoluteLimitAndStalledWorkExpiresSooner()
    {
        var progress = new GuideJumpEvidenceProgress(); progress.Begin(0);
        progress.MadeProgress(.4);
        Assert.False(progress.Expired(.8)); Assert.True(progress.Expired(.91));
        progress.Begin(0);
        for (var time = .25; time <= 2; time += .25)
        { Assert.False(progress.Expired(time)); progress.MadeProgress(time); }
        Assert.True(progress.Expired(2.001));
        Assert.True(progress.Expired(double.NaN)); Assert.True(progress.Expired(-1));
    }

    [Fact]
    public void CriticalProofFreshnessUsesItsOldestQueryAndRejectsClockDiscontinuity()
    {
        Assert.True(GuideJumpEvidenceProgress.Fresh(1, 1.25));
        Assert.False(GuideJumpEvidenceProgress.Fresh(1, 1.2501));
        Assert.False(GuideJumpEvidenceProgress.Fresh(1, .99));
        Assert.False(GuideJumpEvidenceProgress.Fresh(double.NaN, 1));
    }
}
