using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionTeleportMotionTests
{
    private static readonly Vector3[] Route = [Vector3.Zero, new(100, 0, 0)];
    private static readonly CompanionIntentFrame Intent = new(CompanionIntentMode.Returning, 1, 0, true, 9);
    private static readonly GuideJumpCapability Capability = new(GuideJumpAnimationKind.ImportedClip, 0, "Jump", .8f, .12f, .5f);
    private static CompanionTeleportFrame Cue(Vector3? goal = null) => new(CompanionTeleportMode.Corral,
        goal ?? new Vector3(10, 0, 0), default, 1, false, true, false);
    private static CompanionJumpTraversal Walking() => new((_, _, _, _, _, _) => default, (_, _) => GuideJumpEvidence.Clear);
    private static GuideJumpPlan Plan(Vector3[] route, float arc, int direction, GuideJumpCapability capability)
    {
        Assert.True(GuideJumpPolicy.TryRequest(route, arc, direction, capability, out var request));
        var floor = Enumerable.Range(0, GuideJumpRequest.FloorSampleCount)
            .Select(i => new GuideJumpFloor(GuideJumpEvidence.Clear,
                request!.Point(i) + Vector3.UnitY * (i is >= 6 and <= 10 ? .2f : 0), 1)).ToArray();
        return Assert.IsType<GuideJumpPlan>(GuideJumpPolicy.Evaluate(request!, floor).Plan);
    }
    private static CompanionJumpTraversal Jumping() => new((_, route, arc, direction, cap, _) =>
        new(GuideJumpEvidence.Approved, Plan(route, arc, direction, cap), "approved"), (_, _) => GuideJumpEvidence.Clear);
    private static void StartJump(CompanionRouteMotion motion, CompanionJumpTraversal jump)
    {
        jump.Update(motion, 100, Route, 2, Intent, Capability, 0, true);
        jump.ResolvePlacement(motion, true);
        jump.Update(motion, 100, Route, 10, Intent, Capability, .1, true);
        jump.ResolvePlacement(motion, true);
        Assert.True(jump.Active);
    }

    [Fact]
    public void UnspawnedGuideSeedsAtQueryOriginAndRefusalDoesNotWalkOrSpawnAtDestination()
    {
        var motion = new CompanionRouteMotion(); var jump = Walking();
        using var approach = new CompanionApproachCoordinator((from, goal, _) =>
            Task.FromResult<IReadOnlyList<Vector3>>([from, goal]));
        var controller = new CompanionTeleportMotion(approach.Select);
        var player = new Vector3(2, 0, 0);
        var seed = controller.Update(motion, jump, 100, player, Cue(), Intent, default, 0, true);
        Assert.True(seed.MotionSubmitted); Assert.Equal(0, seed.Motion.Arc); Assert.Equal(player, seed.Motion.Position);
        jump.ResolvePlacement(motion, false); Assert.False(motion.Current.HasPosition);
        var retry = controller.Update(motion, jump, 100, player, Cue(), Intent, default, .1, true);
        Assert.Equal(player, retry.Motion.Position); Assert.Equal(0, retry.Motion.Arc);
        jump.ResolvePlacement(motion, true);
        var walking = controller.Update(motion, jump, 100, player, Cue(), Intent, default, .15, true);
        Assert.InRange(walking.Motion.Arc, .001f, .011f);
        jump.ResolvePlacement(motion, false);
        Assert.Equal(player, motion.Current.Position);
    }

    [Fact]
    public void PendingQueryUsesAcceptedOriginAndHoldRejectsAnUnacknowledgedStep()
    {
        var motion = new CompanionRouteMotion(); var jump = Walking();
        jump.Update(motion, 100, Route, 2, Intent, default, 0, true); jump.ResolvePlacement(motion, true);
        jump.Update(motion, 100, Route, 80, Intent, default, .1, true);
        Assert.True(motion.Current.Arc > 2); // Caller forgot to acknowledge this proposal.
        var pending = new TaskCompletionSource<IReadOnlyList<Vector3>>();
        var origins = new List<Vector3>();
        using var approach = new CompanionApproachCoordinator((from, _, _) => { origins.Add(from); return pending.Task; });
        var controller = new CompanionTeleportMotion(approach.Select);
        var held = controller.Update(motion, jump, 100, Vector3.Zero, Cue(), Intent, default, .2, true);
        Assert.Equal(new Vector3(2, 0, 0), Assert.Single(origins));
        Assert.Equal(2, held.Motion.Arc); Assert.False(held.MotionSubmitted); Assert.Equal(0, held.Motion.Velocity);
        for (var i = 1; i <= 5; ++i)
            Assert.Equal(2, controller.Update(motion, jump, 100, Vector3.Zero, Cue(), Intent, default, i, true).Motion.Arc);
        pending.SetResult([new(2, 0, 0), new(10, 0, 0)]);
        var resumed = controller.Update(motion, jump, 100, Vector3.Zero, Cue(), Intent, default, 5.05, true);
        Assert.True(resumed.MotionSubmitted); Assert.InRange(resumed.Motion.Arc, .001f, .011f);
    }

    [Fact]
    public void OwnedJumpBypassesQueriesAndRebasesEvenWhenCueDisappearsOrGesturesBlockWalking()
    {
        var motion = new CompanionRouteMotion(); var jump = Jumping(); StartJump(motion, jump);
        var plan = jump.Frame!.Value.Plan;
        var controller = new CompanionTeleportMotion((_, _, _, _, _, _) => throw new Xunit.Sdk.XunitException("No query while airborne."));
        var result = controller.Update(motion, jump, 100, new(90, 0, 0), default, Intent, Capability, .2, false);
        Assert.True(result.MotionSubmitted); Assert.Same(Route, result.Motion.Route);
        Assert.Equal(plan.Sample(.125f), jump.Frame); Assert.False(result.AtDestination);
        jump.ResolvePlacement(motion, false);
        Assert.Equal(plan.Request.StartArc, motion.Current.Arc);
        Assert.Equal(0, jump.Frame!.Value.Progress);
    }

    [Fact]
    public void QueryCanStartOnlyAfterLandingPlacementIsAcceptedAndUsesItsExactPosition()
    {
        var motion = new CompanionRouteMotion(); var jump = Jumping(); StartJump(motion, jump);
        var queries = new List<Vector3>();
        using var approach = new CompanionApproachCoordinator((from, to, _) =>
        { queries.Add(from); return Task.FromResult<IReadOnlyList<Vector3>>([from, to]); });
        var controller = new CompanionTeleportMotion(approach.Select);
        var now = .1;
        do
        {
            now += .05;
            controller.Update(motion, jump, 100, Vector3.Zero, Cue(), Intent, Capability, now, true);
            Assert.Empty(queries);
            if (jump.Frame is { Complete: true }) break;
            jump.ResolvePlacement(motion, true);
            Assert.True(now < 2);
        } while (true);
        var landing = jump.Frame!.Value;
        jump.ResolvePlacement(motion, false);
        Assert.True(jump.Active); Assert.True(jump.Frame!.Value.Progress < 1);
        now += .05;
        controller.Update(motion, jump, 100, Vector3.Zero, Cue(), Intent, Capability, now, true);
        Assert.Empty(queries); Assert.True(jump.Frame!.Value.Complete);
        jump.ResolvePlacement(motion, true); Assert.False(jump.Active);
        var adopted = controller.Update(motion, jump, 100, Vector3.Zero, Cue(), Intent, default, now, true);
        Assert.Equal(landing.GroundPosition, Assert.Single(queries));
        Assert.Equal(landing.GroundPosition, adopted.Motion.Position); Assert.Equal(0, adopted.Motion.Arc);
    }

    [Fact]
    public void ExistingGuideAdoptsExactStartAtSelfCrossingWithoutNearestBranchProjection()
    {
        var motion = new CompanionRouteMotion(); var jump = Walking();
        jump.Update(motion, 100, Route, 2, Intent, default, 0, true); jump.ResolvePlacement(motion, true);
        var start = motion.Current.Position;
        using var approach = new CompanionApproachCoordinator((_, goal, _) => Task.FromResult<IReadOnlyList<Vector3>>(
            [start, start + Vector3.UnitZ, start, goal]));
        var controller = new CompanionTeleportMotion(approach.Select);
        var result = controller.Update(motion, jump, 100, Vector3.Zero, Cue(), Intent, default, 0, true);
        Assert.True(result.MotionSubmitted); Assert.Equal(0, result.Motion.Arc); Assert.Equal(start, result.Motion.Position);
        Assert.NotSame(Route, result.Motion.Route);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedRouteWithoutSafeExplicitZeroRebaseCannotMove(bool wrongOrigin)
    {
        var motion = new CompanionRouteMotion(); var jump = Walking();
        jump.Update(motion, 100, Route, 2, Intent, default, 0, true); jump.ResolvePlacement(motion, true);
        Vector3[] different = [new(wrongOrigin ? 3 : 2, 0, 0), new(10, 0, 0)];
        var controller = new CompanionTeleportMotion((_, _, _, _, _, _) =>
            new(different, PathTrail.Length(different), wrongOrigin ? 0 : null, false, true, false));
        var held = controller.Update(motion, jump, 100, Vector3.Zero, Cue(), Intent, default, .1, true);
        Assert.False(held.MotionSubmitted); Assert.Same(Route, held.Motion.Route); Assert.Equal(2, held.Motion.Arc);
    }

    [Fact]
    public void GestureBlockAndValidatedArrivalHoldClockWithoutAcceptingOrMoving()
    {
        var motion = new CompanionRouteMotion(); var jump = Walking();
        jump.Update(motion, 100, Route, 2, Intent, default, 0, true); jump.ResolvePlacement(motion, true);
        var arrived = false;
        var controller = new CompanionTeleportMotion((_, _, _, accepted, _, _) =>
            new(accepted.Route, 100, null, arrived, true, arrived));
        Assert.False(controller.Update(motion, jump, 100, Vector3.Zero, Cue(), Intent, default, 2, false).MotionSubmitted);
        arrived = true;
        var atGoal = controller.Update(motion, jump, 100, Vector3.Zero, Cue(), Intent, default, 3, true);
        Assert.True(atGoal.AtDestination); Assert.False(atGoal.MotionSubmitted); Assert.Equal(2, atGoal.Motion.Arc);
        arrived = false;
        var next = controller.Update(motion, jump, 100, Vector3.Zero, Cue(), Intent, default, 3.05, true);
        Assert.InRange(next.Motion.Arc - 2, .001f, .011f);
    }

    [Fact]
    public void InactiveCueAndSelectorFailureNeverSubmitOrQueryPlayerMovement()
    {
        var queries = 0;
        var controller = new CompanionTeleportMotion((_, _, _, _, _, _) => { queries++; throw new InvalidOperationException(); });
        var motion = new CompanionRouteMotion(); var jump = Walking();
        Assert.False(controller.Update(motion, jump, 100, Vector3.Zero, default, Intent, default, 0, true).MotionSubmitted);
        Assert.Equal(0, queries);
        Assert.False(controller.Update(motion, jump, 100, Vector3.Zero, Cue(), Intent, default, .1, true).MotionSubmitted);
        Assert.Equal(1, queries); Assert.False(motion.Current.HasPosition);
    }

}
