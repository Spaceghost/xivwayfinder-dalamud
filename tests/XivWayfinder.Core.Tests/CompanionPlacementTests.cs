using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionPlacementTests
{
    private static readonly Vector3[] Path = [Vector3.Zero, new(100, 0, 0)];
    private static readonly CompanionIntentFrame Lead = new(CompanionIntentMode.Lead, 4, 1, true);

    [Fact]
    public void AcceptedPlacementKeepsItsMotionAndCannotBeRejectedTwice()
    {
        var motion = new CompanionRouteMotion();
        motion.Update(Path, 0, Lead, 0, true);
        motion.ResolvePlacement(true);
        var proposed = motion.Update(Path, 0, Lead, .1, true);
        Assert.True(proposed.Arc > 0);
        Assert.Equal(proposed, motion.ResolvePlacement(true));
        Assert.Equal(proposed, motion.ResolvePlacement(false));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void RejectedStepsKeepExactPositionAndResumeGentlyWithoutTimeBacklog(int fps)
    {
        var motion = new CompanionRouteMotion();
        var initial = motion.Update(Path, 5, Lead, 0, true);
        motion.ResolvePlacement(true);
        for (var i = 1; i <= fps * 4; ++i)
        {
            motion.Update(Path, 5 + i * 6f / fps, Lead, (double)i / fps, true);
            var held = motion.ResolvePlacement(false);
            Assert.Same(Path, held.Route);
            Assert.Equal(initial.Arc, held.Arc);
            Assert.Equal(initial.Position, held.Position);
            Assert.Equal(0, held.Velocity);
        }
        var resumed = motion.Update(Path, 29, Lead, 4 + 1d / fps, true);
        Assert.InRange(resumed.Velocity, 0, CompanionRouteMotion.Acceleration / fps + .0001f);
        Assert.InRange(resumed.Arc - initial.Arc, 0, CompanionRouteMotion.Acceleration / (2 * fps * fps) + .0001f);
        motion.ResolvePlacement(true);
        Assert.Equal(resumed, motion.Update(Path, 29, Lead, 4 + 1d / fps, true));
    }

    [Fact]
    public void RejectionAfterARealAdvanceReturnsToLastAcceptedOccurrenceNotOriginalSeed()
    {
        var motion = new CompanionRouteMotion();
        motion.Update(Path, 2, Lead, 0, true);
        motion.ResolvePlacement(true);
        var accepted = motion.Update(Path, 2, Lead, .1, true);
        motion.ResolvePlacement(true);
        Assert.True(motion.Update(Path, 2, Lead, .2, true).Arc > accepted.Arc);
        var held = motion.ResolvePlacement(false);
        Assert.Equal(accepted.Position, held.Position);
        Assert.Equal(accepted.Arc, held.Arc);
        Assert.Equal(0, held.Velocity);
    }

    [Fact]
    public void RejectedInitialPlacementDoesNotInventAnAcceptedPosition()
    {
        var motion = new CompanionRouteMotion();
        Assert.True(motion.Update(Path, 3, Lead, 0, true).HasPosition);
        var rejected = motion.ResolvePlacement(false);
        Assert.False(rejected.HasPosition);
        Assert.Empty(rejected.Route);
        var retry = motion.Update(Path, 3, Lead, .1, true);
        Assert.Equal(3, retry.Arc);
        Assert.Equal(0, retry.Velocity);
    }

    [Fact]
    public void RejectedRepathRestoresOldRouteOccurrenceWithoutAProjection()
    {
        Vector3[] changed = [new(0, 0, .1f), new(100, 0, .1f)];
        var motion = new CompanionRouteMotion();
        var before = motion.Update(Path, 5, Lead, 0, true);
        motion.ResolvePlacement(true);
        Assert.Same(changed, motion.Update(changed, 5, Lead, .1, true).Route);
        var rejected = motion.ResolvePlacement(false);
        Assert.Same(Path, rejected.Route);
        Assert.Equal(before.Arc, rejected.Arc);
        Assert.Equal(before.Position, rejected.Position);
    }

    [Fact]
    public void RejectedHeldPoseCannotEraseTheOutstandingConnectorRequest()
    {
        Vector3[] disconnected = [new(0, 0, 10), new(100, 0, 10)];
        var motion = new CompanionRouteMotion();
        motion.Update(Path, 5, Lead, 0, true);
        motion.ResolvePlacement(true);
        for (var i = 1; i <= 3; ++i)
        {
            Assert.True(motion.Update(disconnected, 5, Lead, i * .1, true).NeedsConnection);
            var rejected = motion.ResolvePlacement(false);
            Assert.True(rejected.NeedsConnection);
            Assert.Same(Path, rejected.Route);
            Assert.Equal(new Vector3(5, 0, 0), rejected.Position);
        }
    }

    [Fact]
    public void ExactConnectorRebaseThenRejectionKeepsConnectorStartAtSamePosition()
    {
        Vector3[] connector = [new(5, 0, 0), new(5, 0, 10), new(15, 0, 10)];
        var motion = new CompanionRouteMotion();
        motion.Update(Path, 5, Lead, 0, true);
        motion.ResolvePlacement(true);
        Assert.True(motion.TryRebase(connector, 0));
        var before = motion.Current;
        Assert.True(motion.Update(connector, 10, Lead, .1, true).Arc > 0);
        var rejected = motion.ResolvePlacement(false);
        Assert.Same(connector, rejected.Route);
        Assert.Equal(0, rejected.Arc);
        Assert.Equal(before.Position, rejected.Position);
        Assert.False(rejected.NeedsConnection);
    }

    [Fact]
    public void ResetAndRebaseInvalidateStalePlacementAcknowledgements()
    {
        var motion = new CompanionRouteMotion();
        motion.Update(Path, 5, Lead, 0, true);
        motion.Reset();
        Assert.False(motion.ResolvePlacement(false).HasPosition);
        motion.Update(Path, 5, Lead, 1, true);
        motion.Update(Path, 5, Lead, 1.1, true);
        var at = motion.Current;
        Assert.True(motion.TryRebase(Path, at.Arc));
        Assert.Equal(at.Position, motion.ResolvePlacement(false).Position);
    }
}
