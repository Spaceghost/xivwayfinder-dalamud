using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuideJumpTests
{
    private static readonly Vector3[] Route = [Vector3.Zero, new(10, 0, 0), new(10, 0, 10)];
    private static readonly GuideJumpCapability Cap = new(GuideJumpAnimationKind.ImportedClip, 0, "Jump", .8f, .12f, .5f);
    private static readonly CompanionIntentFrame Lead = new(CompanionIntentMode.Lead, 4, 1, true);

    private static GuideJumpRequest Request(float at = 2, int direction = 1)
    {
        Assert.True(GuideJumpPolicy.TryRequest(Route, at, direction, Cap, out var result));
        return result!;
    }
    private static GuideJumpFloor[] Floors(GuideJumpRequest request, float height = .2f)
    {
        var result = new GuideJumpFloor[GuideJumpRequest.FloorSampleCount];
        for (var i = 0; i < result.Length; ++i)
            result[i] = new(GuideJumpEvidence.Clear, request.Point(i) + Vector3.UnitY * (i is >= 6 and <= 10 ? height : 0), 1);
        return result;
    }
    private static GuideJumpPlan Plan()
    {
        var request = Request();
        return Assert.IsType<GuideJumpPlan>(GuideJumpPolicy.Evaluate(request, Floors(request)).Plan);
    }

    [Fact]
    public void MissingAuthoredCapabilityCannotCreateAJump()
    {
        Assert.False(GuideJumpPolicy.TryRequest(Route, 2, 1, default, out _));
        Assert.False(GuideJumpPolicy.TryRequest(Route, 2, 1, Cap with { Kind = GuideJumpAnimationKind.None }, out _));
        Assert.False(GuideJumpPolicy.TryRequest(Route, 2, 1, Cap with { Duration = float.NaN }, out _));
        Assert.False(GuideJumpPolicy.TryRequest(Route, 2, 1, Cap with { Radius = 2 }, out _));
    }

    [Fact]
    public void ExistingCornerAndRouteEndpointCannotBeShortcutted()
    {
        Assert.False(GuideJumpPolicy.TryRequest(Route, 9.5f, 1, Cap, out _));
        Assert.False(GuideJumpPolicy.TryRequest(Route, 10.5f, -1, Cap, out _));
        Assert.False(GuideJumpPolicy.TryRequest(Route, .2f, -1, Cap, out _));
        Assert.False(GuideJumpPolicy.TryRequest(Route, 19.5f, 1, Cap, out _));
        Assert.True(GuideJumpPolicy.TryRequest(Route, 10, 1, Cap, out _));
    }

    [Fact]
    public void JumpRequiresAnActualLowObstacleAndSeparateBodyProof()
    {
        var request = Request();
        var empty = GuideJumpPolicy.Evaluate(request, Floors(request, 0));
        Assert.Equal(GuideJumpEvidence.Clear, empty.Evidence); Assert.Null(empty.Plan);
        var candidate = GuideJumpPolicy.Evaluate(request, Floors(request));
        Assert.Equal(GuideJumpEvidence.Unknown, candidate.Evidence);
        Assert.NotNull(candidate.Plan); // Geometry is not yet body approval.
        Assert.InRange(candidate.Plan.Apex, .2f, GuideJumpPolicy.MaximumApex);
    }

    [Theory]
    [InlineData(GuideJumpEvidence.Unknown, GuideJumpEvidence.Unknown)]
    [InlineData(GuideJumpEvidence.Blocked, GuideJumpEvidence.Blocked)]
    public void MissingGroundEvidenceNeverMeansAJumpableGap(GuideJumpEvidence sample, GuideJumpEvidence expected)
    {
        var request = Request(); var floors = Floors(request);
        floors[8] = floors[8] with { Evidence = sample };
        var result = GuideJumpPolicy.Evaluate(request, floors);
        Assert.Equal(expected, result.Evidence); Assert.Null(result.Plan);
    }

    [Theory]
    [InlineData(-.2f)]
    [InlineData(.7f)]
    public void DipsAndLargeObstaclesFailClosed(float height)
    {
        var request = Request(); var result = GuideJumpPolicy.Evaluate(request, Floors(request, height));
        Assert.Equal(GuideJumpEvidence.Blocked, result.Evidence); Assert.Null(result.Plan);
    }

    [Fact]
    public void TakeoffLandingMarginsAndMatchingFloorAreRequired()
    {
        var request = Request(); var floors = Floors(request);
        floors[1] = floors[1] with { Position = floors[1].Position + Vector3.UnitY * .2f };
        Assert.Null(GuideJumpPolicy.Evaluate(request, floors).Plan);
        floors = Floors(request); floors[^1] = floors[^1] with { Position = floors[^1].Position + Vector3.UnitY * .5f };
        Assert.Null(GuideJumpPolicy.Evaluate(request, floors).Plan);
        floors = Floors(request); floors[8] = floors[8] with { NormalY = 0 };
        Assert.Null(GuideJumpPolicy.Evaluate(request, floors).Plan);
        floors = Floors(request); floors[8] = floors[8] with { Position = floors[8].Position + Vector3.UnitZ };
        Assert.Null(GuideJumpPolicy.Evaluate(request, floors).Plan);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void AllJumpFeetStayOnTheirOwnRouteAndReturnToConfirmedLanding(int direction)
    {
        var request = Request(5, direction);
        var plan = GuideJumpPolicy.Evaluate(request, Floors(request)).Plan!;
        for (var i = 0; i <= 100; ++i)
        {
            var frame = plan.Sample(i / 100f);
            Assert.Equal(PathTrail.At(Route, frame.Arc), frame.GroundPosition);
            Assert.Equal(frame.GroundPosition.X, frame.FeetPosition.X);
            Assert.Equal(frame.GroundPosition.Z, frame.FeetPosition.Z);
            Assert.InRange(frame.FeetPosition.Y, .034f, GuideJumpPolicy.MaximumApex + .036f);
        }
        Assert.True(plan.Sample(1).Complete);
        Assert.Equal(plan.Landing.Y + GuideJumpPolicy.FootClearance, plan.Sample(1).FeetPosition.Y);
    }

    [Fact]
    public void RouteMutationInvalidatesPreparedEvidence()
    {
        Vector3[] mutable = [Vector3.Zero, new(10, 0, 0)];
        Assert.True(GuideJumpPolicy.TryRequest(mutable, 2, 1, Cap, out var request));
        mutable[1] += Vector3.UnitZ;
        Assert.False(request!.MatchesRoute());
        Assert.Null(GuideJumpPolicy.Evaluate(request, Floors(request)).Plan);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void MotionAcceptsOnlyItsOwnedRouteAndMonotonicClockBoundedJumpSamples(int fps)
    {
        var plan = Plan(); var motion = new CompanionRouteMotion();
        motion.Update(Route, plan.Request.StartArc, Lead, 0, true); motion.ResolvePlacement(true);
        Assert.True(motion.TryApplyJumpFrame(plan.Sample(0), 0)); motion.ResolvePlacement(true);
        for (var i = 1; i <= fps; ++i)
        {
            var progress = Math.Min(1, i / (float)fps / plan.Duration);
            Assert.True(motion.TryApplyJumpFrame(plan.Sample(progress), i / (double)fps));
            motion.ResolvePlacement(true);
            Assert.Equal(plan.Sample(progress).GroundPosition, motion.Current.Position);
        }
        Assert.Equal(plan.Request.EndArc, motion.Current.Arc);
        Assert.False(motion.TryApplyJumpFrame(plan.Sample(.5f), 1.01));
    }

    [Fact]
    public void FailedJumpPlacementRollsBackArcAndProgressWithoutCatchup()
    {
        var plan = Plan(); var motion = new CompanionRouteMotion();
        motion.Update(Route, plan.Request.StartArc, Lead, 0, true); motion.ResolvePlacement(true);
        Assert.True(motion.TryApplyJumpFrame(plan.Sample(0), 0)); motion.ResolvePlacement(true);
        Assert.True(motion.TryApplyJumpFrame(plan.Sample(.1f), .08));
        motion.ResolvePlacement(false);
        Assert.Equal(plan.Request.StartArc, motion.Current.Arc);
        Assert.False(motion.TryApplyJumpFrame(plan.Sample(.2f), .09));
        Assert.True(motion.TryApplyJumpFrame(plan.Sample(.1f), .16));
        motion.ResolvePlacement(true);
    }

    [Fact]
    public void InitialJumpCannotSnapToAnotherArcOrForgedFootPose()
    {
        var plan = Plan(); var motion = new CompanionRouteMotion();
        motion.Update(Route, 0, Lead, 0, true);
        Assert.False(motion.TryApplyJumpFrame(plan.Sample(0), 0));
        motion.Reset(); motion.Update(Route, plan.Request.StartArc, Lead, 0, true);
        Assert.False(motion.TryApplyJumpFrame(plan.Sample(.2f), .1));
        Assert.False(motion.TryApplyJumpFrame(plan.Sample(0) with { FeetPosition = new(900) }, 0));
    }

    [Fact]
    public void MalformedOrMutatedPlansCannotBeSampledOrAdopted()
    {
        var valid = Plan();
        foreach (var plan in new[] { valid with { Apex = float.NaN }, valid with { Apex = 100 },
            valid with { Takeoff = new(200) }, valid with { Landing = new(float.NaN) } })
        {
            Assert.False(plan.Valid);
            Assert.Throws<InvalidOperationException>(() => plan.Sample(0));
            var motion = new CompanionRouteMotion(); motion.Update(Route, valid.Request.StartArc, Lead, 0, true);
            Assert.False(motion.TryApplyJumpFrame(valid.Sample(0) with { Plan = plan }, 0));
        }
    }

    [Fact]
    public void NativeLandingFinishesItsAuthoredPhaseOnTheConfirmedGroundWithoutAdvancingPastTheObstacle()
    {
        var request = Request() with
        {
            Capability = Cap with
            {
                Kind = GuideJumpAnimationKind.NativeTimeline,
                AnimationId = 31,
                Duration = GuideNativeJumpSet.LowHopSeconds
            }
        };
        var plan = GuideJumpPolicy.Evaluate(request, Floors(request)).Plan!;
        Assert.Equal(GuideJumpPhase.Takeoff, plan.Sample(.05f / plan.Duration).Phase);
        Assert.Equal(GuideJumpPhase.Airborne, plan.Sample(.3f / plan.Duration).Phase);
        var landed = plan.Sample((GuideNativeJumpSet.FlightSeconds + .001f) / plan.Duration);
        Assert.Equal(GuideJumpPhase.Landing, landed.Phase);
        Assert.Equal(request.EndArc, landed.Arc);
        Assert.Equal(plan.Landing.Y + GuideJumpPolicy.FootClearance, landed.FeetPosition.Y);
        var held = plan.Sample(.99f);
        Assert.Equal(landed.Arc, held.Arc); Assert.Equal(landed.FeetPosition, held.FeetPosition);
        Assert.False(held.Complete); Assert.True(plan.Sample(1).Complete);
    }
}
