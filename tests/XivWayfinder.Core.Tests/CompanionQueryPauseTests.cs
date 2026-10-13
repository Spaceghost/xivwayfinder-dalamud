using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionQueryPauseTests
{
    private static readonly Vector3[] Route = [Vector3.Zero, new(300, 0, 0)];
    private static readonly CompanionIntentFrame Intent = new(CompanionIntentMode.Returning, 1, 0, true, 9);

    private static CompanionRouteMotion Moving()
    {
        var motion = new CompanionRouteMotion();
        motion.Update(Route, 0, Intent, 0, true); motion.ResolvePlacement(true);
        for (var i = 1; i <= 60; ++i)
        { motion.Update(Route, 200, Intent, i / 60.0, true); motion.ResolvePlacement(true); }
        Assert.InRange(motion.Current.Velocity, 7.99f, 8.01f);
        return motion;
    }

    [Fact]
    public void ShortPendingQueryFreezesOriginAndDisplayedVelocityWithoutBankingElapsedTravel()
    {
        var motion = Moving(); var before = motion.Current;
        foreach (var now in new[] { 1.02, 1.04, 1.06, 1.08, 1.1 })
        {
            var held = motion.HoldQueryClock(now);
            Assert.Equal(before.Position, held.Position); Assert.Equal(before.Arc, held.Arc);
            Assert.Equal(0, held.Velocity);
        }
        var next = motion.Update(Route, 200, Intent, 1.12, true);
        Assert.InRange(next.Velocity, 7.18f, 7.22f); // accepted 8 minus .1s decay; current .02s reaccelerates
        Assert.InRange(next.Arc - before.Arc, .13f, CompanionRouteMotion.MaximumSpeed * .02f);
    }

    [Theory]
    [InlineData(1f, 0f, 7.2f)]
    [InlineData(0f, 1f, .16f)]
    [InlineData(.6f, .8f, 4.32f)] // project first, then decay/reaccelerate along the new tangent
    public void ExactRebaseProjectsCachedMomentumOntoReturnedTangent(float x, float z, float expected)
    {
        var motion = Moving(); var start = motion.Current.Position;
        motion.HoldQueryClock(1.02); motion.HoldQueryClock(1.1);
        Vector3[] returned = [start, start + new Vector3(x, 0, z) * 100];
        Assert.True(motion.TryRebase(returned, 0));
        Assert.Equal(start, motion.Current.Position); Assert.Equal(0, motion.Current.Velocity);
        var next = motion.Update(returned, 100, Intent, 1.12, true);
        Assert.InRange(next.Velocity, expected - .02f, expected + .02f);
        Assert.Equal(PathTrail.At(returned, next.Arc), next.Position);
        Assert.InRange(Vector3.Distance(start, next.Position), 0, CompanionRouteMotion.MaximumSpeed * .02f);
    }

    [Fact]
    public void RejectedResumeClearsMomentumAndCannotRepeatAFastRefusedStep()
    {
        var motion = Moving(); var start = motion.Current.Position;
        motion.HoldQueryClock(1.02); motion.HoldQueryClock(1.1);
        motion.Update(Route, 200, Intent, 1.12, true);
        var rejected = motion.ResolvePlacement(false);
        Assert.Equal(start, rejected.Position); Assert.Equal(0, rejected.Velocity);
        var next = motion.Update(Route, 200, Intent, 1.14, true);
        Assert.InRange(next.Velocity, .15f, .17f);
        Assert.InRange(Vector3.Distance(start, next.Position), 0, .002f);
    }

    [Fact]
    public void UnacknowledgedProposalCannotContributeQueryMomentum()
    {
        var motion = Moving(); var start = motion.Current.Position;
        motion.Update(Route, 200, Intent, 1.02, true);
        motion.HoldQueryClock(1.04);
        Assert.Equal(start, motion.Current.Position);
        var next = motion.Update(Route, 200, Intent, 1.06, true);
        Assert.InRange(next.Velocity, .15f, .17f);
    }

    [Fact]
    public void InvalidFirstQueryClockCannotAdvertiseMotionOrRetainSpeed()
    {
        var motion = Moving(); var start = motion.Current.Position;
        var held = motion.HoldQueryClock(double.NaN);
        Assert.Equal(start, held.Position); Assert.Equal(0, held.Velocity);
        var next = motion.Update(Route, 200, Intent, 1.02, true);
        Assert.InRange(next.Velocity, .15f, .17f);
    }

    [Theory]
    [InlineData("long")]
    [InlineData("cancelled")]
    [InlineData("backwards")]
    [InlineData("invalid")]
    [InlineData("discontinuity")]
    public void LongOrNonQueryHoldsAndClockDiscontinuitiesForgetMomentum(string cause)
    {
        var motion = Moving(); var start = motion.Current.Position;
        motion.HoldQueryClock(1.02); motion.HoldQueryClock(1.1);
        double resumeAt;
        switch (cause)
        {
            case "long": motion.HoldQueryClock(1.18); resumeAt = 1.2; break;
            case "cancelled": motion.HoldJumpClock(1.1); resumeAt = 1.12; break;
            case "backwards": motion.HoldQueryClock(1.05); resumeAt = 1.07; break;
            case "invalid": motion.HoldQueryClock(double.NaN); resumeAt = 1.12; break;
            default: motion.HoldQueryClock(5); resumeAt = 5.02; break;
        }
        var next = motion.Update(Route, 200, Intent, resumeAt, true);
        Assert.InRange(next.Velocity, .15f, .17f);
        Assert.InRange(Vector3.Distance(start, next.Position), 0, .002f);
    }

    [Fact]
    public void PendingEvidenceExcludesCompletedFailedCancelledOrThrottledRequests()
    {
        var motion = Moving(); var work = new TaskCompletionSource<IReadOnlyList<Vector3>>();
        using var approach = new CompanionApproachCoordinator((_, _, _) => work.Task);
        var goal = new Vector3(20, 0, 0);
        Assert.True(approach.Select(100, goal, Vector3.Zero, motion.Current, 1, true).QueryPending);
        Assert.False(approach.Select(100, goal, Vector3.Zero, motion.Current, 1.01, false).QueryPending);
        Assert.True(approach.QueryInFlight); // Abandoned provider still owns the single-flight slot.
        Assert.False(approach.Select(100, goal, Vector3.Zero, motion.Current, 1.02, true).QueryPending);
        work.SetException(new InvalidOperationException());
        Assert.False(approach.Select(100, goal, Vector3.Zero, motion.Current, 1.03, true).QueryPending);
        using var failed = new CompanionApproachCoordinator((_, _, _) => throw new InvalidOperationException());
        Assert.False(failed.Select(100, goal, Vector3.Zero, motion.Current, 1, true).QueryPending);
        Assert.False(failed.Select(100, goal, Vector3.Zero, motion.Current, 1.1, true).QueryPending);
    }
}
