using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionRouteMotionTests
{
    private static readonly Vector3[] Straight = [Vector3.Zero, new(100,0,0)];
    private static readonly CompanionIntentFrame Lead = new(CompanionIntentMode.Lead, 4, 1, true);
    private static readonly CompanionIntentFrame Return = new(CompanionIntentMode.Returning, 1, 0, true);

    [Fact]
    public void StartsAtPlayerThenWalksIndependentlyIntoLeadBandAndWaits()
    {
        var motion = new CompanionRouteMotion();
        Assert.Equal(10, motion.Update(Straight, 10, Lead, 0, true).Arc);
        var early = motion.Update(Straight, 10, Lead, .05, true);
        Assert.InRange(early.Arc, 10.001f, 10.1f); // not snapped to player+4
        CompanionRouteMotionFrame frame = default;
        for (var i = 2; i <= 200; i++) frame = motion.Update(Straight, 10, Lead, i * .05, true);
        Assert.InRange(frame.Arc, 16.9f, 17.1f); Assert.InRange(Math.Abs(frame.Velocity), 0, .02f);
        var before = frame.Arc;
        frame = motion.Update(Straight, 11, Lead, 10.05, true);
        Assert.InRange(Math.Abs(frame.Arc - before), 0, .01f); // player moves within the band; guide waits
    }

    [Fact]
    public void GuideCanLagBehindPlayerAndNeverTeleportsToKeepFixedOffset()
    {
        var motion = new CompanionRouteMotion(); motion.Update(Straight, 5, Lead, 0, true);
        var frame = motion.Update(Straight, 20, Lead, .05, true);
        Assert.InRange(frame.Arc, 5, 5.1f); Assert.True(frame.Arc < 20);
        Assert.InRange(frame.Velocity, 0, CompanionRouteMotion.Acceleration * .05f + .001f);
    }

    [Fact]
    public void LoyalReturnReversesSmoothlyAndStopsNearPlayer()
    {
        var motion = new CompanionRouteMotion(); motion.Update(Straight, 10, Lead, 0, true);
        for (var i = 1; i <= 40; i++) motion.Update(Straight, 10, Lead, i * .05, true);
        var previous = motion.Current; var sawNegative = false;
        for (var i = 41; i <= 160; i++)
        {
            var next = motion.Update(Straight, 10, Return, i * .05, true);
            Assert.InRange(Math.Abs(next.Velocity - previous.Velocity), 0, .401f);
            Assert.InRange(Math.Abs(next.Arc - previous.Arc), 0, CompanionRouteMotion.MaximumSpeed * .05f + .001f);
            sawNegative |= next.Velocity < -.1f; previous = next;
        }
        Assert.True(sawNegative); Assert.InRange(previous.Arc, 10.98f, 11.02f);
    }

    [Fact]
    public void AccelerationAndSpeedAreBoundedWhilePlayerMovesAndReverses()
    {
        var motion = new CompanionRouteMotion(); motion.Update(Straight, 10, Lead, 0, true);
        var previous = motion.Current;
        for (var i = 1; i <= 160; i++)
        {
            var next = motion.Update(Straight, i < 80 ? 20 : 5, i < 80 ? Lead : Return, i * .025, true);
            Assert.InRange(Math.Abs(next.Velocity), 0, CompanionRouteMotion.MaximumSpeed + .001f);
            Assert.InRange(Math.Abs(next.Velocity - previous.Velocity), 0, CompanionRouteMotion.Acceleration * .025f + .001f);
            previous = next;
        }
    }

    [Fact]
    public void DifferentFrameRatesProduceTheSameIndependentWalk()
    {
        static CompanionRouteMotionFrame Simulate(int fps)
        {
            var motion = new CompanionRouteMotion(); motion.Update(Straight, 0, Lead, 0, true);
            for (var i = 1; i <= fps * 3; i++) motion.Update(Straight, (float)i / fps * 2, Lead, (double)i / fps, true);
            return motion.Current;
        }
        var slow = Simulate(30); var fast = Simulate(144);
        Assert.InRange(Math.Abs(slow.Arc - fast.Arc), 0, .12f);
        Assert.InRange(Math.Abs(slow.Velocity - fast.Velocity), 0, .12f);
    }

    [Fact]
    public void EveryPositionFollowsBentRouteAndItsHeightNotAStraightCornerShortcut()
    {
        Vector3[] path = [Vector3.Zero, new(5,0,0), new(5,3,8)];
        var motion = new CompanionRouteMotion(); motion.Update(path, 1, Lead, 0, true);
        for (var i = 1; i <= 120; i++)
        {
            var frame = motion.Update(path, 1, Lead, i * .05, true);
            Assert.Equal(PathTrail.At(path, frame.Arc), frame.Position);
            Assert.True(frame.Position.Z == 0 || frame.Position.X == 5);
        }
    }

    [Fact]
    public void SameCorridorRepathPreservesWorldPositionRatherThanOldNumericArc()
    {
        var motion = new CompanionRouteMotion(); motion.Update(Straight, 10, Lead, 0, true);
        Vector3[] changed = [new(5,0,0), new(100,0,0)];
        var next = motion.Update(changed, 5, Lead, 0, true);
        Assert.Equal(5, next.Arc); Assert.Equal(new Vector3(10,0,0), next.Position);
        Assert.Same(changed, next.Route); Assert.False(next.NeedsConnection);
    }

    [Fact]
    public void DisconnectedOrWrongFloorRepathKeepsOldPoseAndRequestsRealConnection()
    {
        var motion = new CompanionRouteMotion(); var first = motion.Update(Straight, 10, Lead, 0, true);
        foreach (var changed in new Vector3[][]
        {
            [new(10,0,10), new(100,0,10)], [new(0,5,0), new(100,5,0)]
        })
        {
            var held = motion.Update(changed, 10, Lead, .05, true);
            Assert.True(held.NeedsConnection); Assert.Same(Straight, held.Route);
            Assert.Equal(first.Position, held.Position); Assert.Equal(0, held.Velocity);
        }
    }

    [Fact]
    public void ConfirmedConnectorCanStartAtOldGuideAndContinueOnNewRoute()
    {
        var motion = new CompanionRouteMotion(); motion.Update(Straight, 10, Lead, 0, true);
        Vector3[] disconnected = [new(10,0,10), new(100,0,10)];
        Assert.True(motion.Update(disconnected, 0, Lead, .05, true).NeedsConnection);
        Vector3[] confirmed = [new(10,0,0), new(10,0,10), new(100,0,10)];
        var joined = motion.Update(confirmed, 10, Lead, .1, true);
        Assert.False(joined.NeedsConnection); Assert.Same(confirmed, joined.Route);
        Assert.InRange(joined.Arc, 0, .1f); Assert.Equal(10, joined.Position.X);
    }

    [Fact]
    public void MissingPathOrLongPauseHoldsPositionAndResetAloneAllowsReseed()
    {
        var motion = new CompanionRouteMotion(); var first = motion.Update(Straight, 10, Lead, 0, true);
        var hidden = motion.Update([], 0, Lead, .05, false);
        Assert.True(hidden.HasPosition); Assert.True(hidden.NeedsConnection); Assert.Equal(first.Position, hidden.Position);
        var paused = motion.Update(Straight, 50, Lead, 10, true);
        Assert.Equal(first.Position, paused.Position); Assert.Equal(0, paused.Velocity);
        motion.Reset(); Assert.False(motion.Current.HasPosition);
        Assert.Equal(50, motion.Update(Straight, 50, Lead, 11, true).Arc);
    }

    [Fact]
    public void EndpointsAndInvalidInputsStayBoundedAndFinite()
    {
        Vector3[] shortPath = [Vector3.Zero, new(1,0,0)];
        var motion = new CompanionRouteMotion(); motion.Update(shortPath, 0, Lead, 0, true);
        for (var i = 1; i <= 100; i++) Assert.InRange(motion.Update(shortPath, 0, Lead, i * .05, true).Arc, 0, 1);
        var before = motion.Current;
        Assert.Equal(before, motion.Update(shortPath, 0, Lead, double.NaN, true));
        Assert.True(motion.Update([Vector3.Zero, new(float.NaN)], 0, Lead, 6, true).NeedsConnection);
        Assert.Equal(before.Position, motion.Current.Position);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void SteadySixYalmRunnerGetsAnActualForwardGuideRatherThanPermanentLag(int fps)
    {
        var motion = new CompanionRouteMotion();
        motion.Update(Straight, 0, Lead, 0, true);
        var previous = motion.Current;
        var observedIndependent = false;
        for (var i = 1; i <= fps * 12; ++i)
        {
            var time = (double)i / fps;
            var playerArc = (float)(6 * time);
            var frame = motion.Update(Straight, playerArc, Lead, time, true);
            Assert.InRange(Math.Abs(frame.Velocity), 0, CompanionRouteMotion.MaximumSpeed + .001f);
            Assert.InRange(Math.Abs(frame.Velocity - previous.Velocity), 0,
                CompanionRouteMotion.Acceleration / fps + .001f);
            Assert.Equal(PathTrail.At(Straight, frame.Arc), frame.Position);
            if (time > 6) Assert.InRange(frame.Arc - playerArc, 2.5f, 8);
            observedIndependent |= time > 2 && Math.Abs(frame.Velocity - 6) > .5f;
            previous = frame;
        }
        Assert.True(observedIndependent, "The guide must run/wait within its band, not copy the player's velocity exactly.");
    }

    [Fact]
    public void ExactArcRebaseAtSelfCrossingChoosesRequestedBranchAndPreservesClock()
    {
        Vector3[] old = [new(-2, 0, 0), new(2, 0, 0)];
        Vector3[] crossed = [Vector3.Zero, new(2, 0, 0), new(2, 0, 2), new(0, 0, 2), Vector3.Zero, new(-2, 0, 0)];
        var motion = new CompanionRouteMotion();
        motion.Update(old, 2, Lead, 5, true);
        Assert.True(motion.TryRebase(crossed, 0));
        Assert.Equal(0, motion.Current.Arc); // Global nearest-point projection would choose the later crossing.
        Assert.Same(crossed, motion.Current.Route);
        Assert.Equal(Vector3.Zero, motion.Current.Position);
        var sameTimestamp = motion.Update(crossed, 0, Lead, 5, true);
        Assert.Equal(Vector3.Zero, sameTimestamp.Position);
        Assert.Equal(0, sameTimestamp.Velocity);
        Assert.True(motion.Update(crossed, 0, Lead, 5.05, true).Arc > 0);
    }

    [Fact]
    public void UnsafeExactArcRebaseLeavesPositionAndMotionUntouched()
    {
        var motion = new CompanionRouteMotion();
        motion.Update(Straight, 5, Lead, 0, true);
        motion.Update(Straight, 5, Lead, .1, true);
        var before = motion.Current;
        Assert.False(motion.TryRebase(Straight, 20));
        Assert.False(motion.TryRebase(Straight, -1));
        Assert.False(motion.TryRebase(Straight, float.NaN));
        Assert.False(motion.TryRebase(Straight, before.Arc, 10));
        Assert.Equal(before, motion.Current);
        Assert.True(motion.TryRebase(Straight, before.Arc));
        Assert.Equal(before.Position, motion.Current.Position);
        Assert.Equal(before.Velocity, motion.Current.Velocity);
    }
}
