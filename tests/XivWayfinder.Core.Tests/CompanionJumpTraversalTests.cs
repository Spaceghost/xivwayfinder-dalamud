using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionJumpTraversalTests
{
    private static readonly Vector3[] Route = [Vector3.Zero, new(100, 0, 0)];
    private static readonly GuideJumpCapability Capability = new(GuideJumpAnimationKind.ImportedClip, 0, "Jump", .8f, .12f, .5f);
    private static readonly CompanionIntentFrame Lead = new(CompanionIntentMode.Lead, 4, 1, true);

    private static GuideJumpPlan Plan(Vector3[] route, float arc, int direction, GuideJumpCapability capability)
    {
        Assert.True(GuideJumpPolicy.TryRequest(route, arc, direction, capability, out var request));
        var floor = Enumerable.Range(0, GuideJumpRequest.FloorSampleCount)
            .Select(i => new GuideJumpFloor(GuideJumpEvidence.Clear,
                request!.Point(i) + Vector3.UnitY * (i is >= 6 and <= 10 ? .2f : 0), 1)).ToArray();
        return Assert.IsType<GuideJumpPlan>(GuideJumpPolicy.Evaluate(request!, floor).Plan);
    }

    private static CompanionJumpTraversal Controller(Func<GuideJumpFrame, GuideJumpFrame, GuideJumpEvidence>? validate = null) =>
        new((_, route, arc, direction, capability, _) => new(GuideJumpEvidence.Approved, Plan(route, arc, direction, capability), "approved"),
            validate ?? ((_, _) => GuideJumpEvidence.Clear));

    private static CompanionRouteMotion Start(CompanionJumpTraversal controller, GuideJumpCapability? capability = null)
    {
        var motion = new CompanionRouteMotion(); var cap = capability ?? Capability;
        controller.Update(motion, 100, Route, 2, Lead, cap, 0, true); controller.ResolvePlacement(motion, true);
        controller.Update(motion, 100, Route, 2, Lead, cap, .1, true);
        Assert.True(controller.Active); Assert.Equal(0, controller.Frame!.Value.Progress);
        controller.ResolvePlacement(motion, true);
        return motion;
    }

    [Fact]
    public void ProbeUsesPreStepOwnedArcAndPendingEvidenceDoesNotWalkThroughTheCandidate()
    {
        var starts = new List<float>(); var attempts = 0;
        var controller = new CompanionJumpTraversal((_, route, arc, direction, cap, _) =>
        {
            starts.Add(arc); Assert.Same(Route, route); Assert.Equal(1, direction);
            return ++attempts < 3 ? new(GuideJumpEvidence.Pending, null, "collecting")
                : new(GuideJumpEvidence.Approved, Plan(route, arc, direction, cap), "approved");
        }, (_, _) => GuideJumpEvidence.Clear);
        var motion = new CompanionRouteMotion();
        controller.Update(motion, 100, Route, 2, Lead, Capability, 0, true); controller.ResolvePlacement(motion, true);
        for (var i = 1; i <= 3; i++)
        {
            controller.Update(motion, 100, Route, 2, Lead, Capability, i * .1, true);
            Assert.Equal(2, motion.Current.Arc);
            controller.ResolvePlacement(motion, true);
        }
        Assert.Equal(new[] { 2f, 2f, 2f }, starts);
        Assert.True(controller.Active);
    }

    [Fact]
    public void NoCapabilityOrNoActualMotionDoesNotRequestCollisionWork()
    {
        var calls = 0;
        var controller = new CompanionJumpTraversal((_, _, _, _, _, _) => { calls++; return default; }, (_, _) => GuideJumpEvidence.Clear);
        var motion = new CompanionRouteMotion();
        controller.Update(motion, 100, Route, 2, Lead, default, 0, true); controller.ResolvePlacement(motion, true);
        controller.Update(motion, 100, Route, 2, Lead, default, .1, true); controller.ResolvePlacement(motion, true);
        Assert.True(motion.Current.Arc > 2); Assert.Equal(0, calls);
        controller.Reset(); motion.Reset();
        controller.Update(motion, 100, Route, 100, Lead, Capability, 0, true); controller.ResolvePlacement(motion, true);
        controller.Update(motion, 100, Route, 100, Lead, Capability, .1, true);
        Assert.Equal(100, motion.Current.Arc); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(GuideJumpEvidence.Unknown)]
    [InlineData(GuideJumpEvidence.Blocked)]
    [InlineData(GuideJumpEvidence.Clear)]
    public void NonApprovalLeavesOrdinaryStepSubjectToNormalPlacementGuards(GuideJumpEvidence evidence)
    {
        var controller = new CompanionJumpTraversal((_, _, _, _, _, _) => new(evidence, null, "not approved"), (_, _) => GuideJumpEvidence.Clear);
        var motion = new CompanionRouteMotion();
        controller.Update(motion, 100, Route, 2, Lead, Capability, 0, true); controller.ResolvePlacement(motion, true);
        controller.Update(motion, 100, Route, 2, Lead, Capability, .1, true);
        Assert.True(motion.Current.Arc > 2); Assert.False(controller.Active); Assert.Null(controller.Frame);
        controller.ResolvePlacement(motion, false); Assert.Equal(2, motion.Current.Arc);
    }

    [Fact]
    public void ActiveJumpDoesNotRunNormalIntegratorOrRebaseToAChangedTargetRoute()
    {
        var controller = Controller(); var motion = Start(controller); var plan = controller.Frame!.Value.Plan;
        Vector3[] changed = [new(0, 0, 30), new(100, 0, 30)];
        controller.Update(motion, 100, changed, 90, Lead with { EffectiveLead = 16 }, Capability, .2, true);
        Assert.Same(Route, motion.Current.Route);
        Assert.Equal(plan.Sample(.125f), controller.Frame);
        Assert.Equal(plan.Sample(.125f).Arc, motion.Current.Arc);
        controller.ResolvePlacement(motion, true);
    }

    [Fact]
    public void OwnedJumpCanFinishWhileMainRouteIsTemporarilyUnavailable()
    {
        var controller = Controller(); var motion = Start(controller); var plan = controller.Frame!.Value.Plan;
        var time = .1;
        while (controller.Active)
        {
            time += .05;
            controller.Update(motion, 100, [], 0, Lead, Capability, time, true);
            Assert.Same(plan.Route, motion.Current.Route);
            Assert.False(motion.Current.NeedsConnection);
            controller.ResolvePlacement(motion, true);
            Assert.True(time < 2);
        }
        Assert.Equal(plan.Request.EndArc, motion.Current.Arc);
    }

    [Fact]
    public void NativeAuthoredLandingHoldsItsArcUntilTheCompletePoseIsAccepted()
    {
        var cap = new GuideJumpCapability(GuideJumpAnimationKind.NativeTimeline, 7, "verified body jump",
            GuideNativeJumpSet.LowHopSeconds, .12f, .5f);
        var controller = Controller(); var motion = Start(controller, cap);
        var plan = controller.Frame!.Value.Plan;
        var time = .1;
        var groundedFrames = 0;
        GuideJumpFrame? complete = null;
        while (controller.Active)
        {
            time += 1d / 60;
            controller.Update(motion, 100, Route, 2, Lead, cap, time, true);
            var frame = controller.Frame!.Value;
            if (frame.Phase == GuideJumpPhase.Landing)
            {
                groundedFrames++;
                Assert.Equal(plan.Request.EndArc, motion.Current.Arc);
                Assert.Equal(plan.Landing.Y + GuideJumpPolicy.FootClearance, frame.FeetPosition.Y, 5);
            }
            if (frame.Complete)
            {
                complete = frame;
                controller.ResolvePlacement(motion, false);
                Assert.True(controller.Active);
                Assert.False(controller.Frame!.Value.Complete);
                Assert.Equal(plan.Request.EndArc, motion.Current.Arc);
                controller.Update(motion, 100, Route, 2, Lead, cap, time + 1d / 60, true);
                Assert.True(controller.Frame!.Value.Complete);
                controller.ResolvePlacement(motion, true);
                break;
            }
            controller.ResolvePlacement(motion, true);
            Assert.True(time < 3);
        }
        Assert.InRange(groundedFrames, 26, 29);
        Assert.False(controller.Active);
        Assert.Null(controller.Frame);
        // The renderer may retain this immutable accepted sample for the UI
        // draw even after the controller releases ownership for the next step.
        Assert.True(complete!.Value.Complete);
        Assert.Equal(plan.Request.EndArc, complete.Value.Arc);
    }

    [Fact]
    public void RejectedPlacementRestoresDisplayedPoseClipTimeAndArcWithoutBankingTime()
    {
        var controller = Controller(); var motion = Start(controller);
        controller.Update(motion, 100, Route, 2, Lead, Capability, .2, true); controller.ResolvePlacement(motion, true);
        var accepted = controller.Frame; var arc = motion.Current.Arc;
        controller.Update(motion, 100, Route, 2, Lead, Capability, .3, true);
        Assert.True(controller.Frame!.Value.Progress > accepted!.Value.Progress);
        controller.ResolvePlacement(motion, false);
        Assert.Equal(accepted, controller.Frame); Assert.Equal(arc, motion.Current.Arc);
        controller.Update(motion, 100, Route, 2, Lead, Capability, .4, true);
        Assert.Equal(.25f, controller.Frame!.Value.Progress, 5);
    }

    [Fact]
    public void UnacknowledgedProposalIsRejectedBeforeTheNextStep()
    {
        var controller = Controller(); var motion = Start(controller);
        controller.Update(motion, 100, Route, 2, Lead, Capability, .2, true); // deliberately no acknowledgement
        controller.Update(motion, 100, Route, 2, Lead, Capability, .3, true);
        Assert.Equal(.125f, controller.Frame!.Value.Progress, 5);
        controller.ResolvePlacement(motion, true);
    }

    [Theory]
    [InlineData(GuideJumpEvidence.Blocked)]
    [InlineData(GuideJumpEvidence.Unknown)]
    [InlineData(GuideJumpEvidence.Pending)]
    public void BlockedOrUnavailableAirborneEvidenceHoldsPhaseAndConsumesTheClock(GuideJumpEvidence evidence)
    {
        var result = evidence;
        var controller = Controller((_, _) => result); var motion = Start(controller); var accepted = controller.Frame;
        for (var i = 2; i <= 30; ++i)
        {
            controller.Update(motion, 100, Route, 2, Lead, Capability, i * .1, true);
            controller.ResolvePlacement(motion, true);
            Assert.Equal(accepted, controller.Frame); Assert.Equal(2, motion.Current.Arc);
        }
        result = GuideJumpEvidence.Clear;
        controller.Update(motion, 100, Route, 2, Lead, Capability, 3.1, true);
        Assert.Equal(.125f, controller.Frame!.Value.Progress, 5);
    }

    [Fact]
    public void LongPauseAndClockRollbackResumeOnTheNextSmallIntervalWithoutBacklog()
    {
        var calls = 0;
        var controller = Controller((_, _) => { calls++; return GuideJumpEvidence.Clear; }); var motion = Start(controller);
        controller.Update(motion, 100, Route, 2, Lead, Capability, 20, true);
        Assert.Equal(0, calls); Assert.Equal(0, controller.Frame!.Value.Progress);
        controller.Update(motion, 100, Route, 2, Lead, Capability, 20.1, true); controller.ResolvePlacement(motion, true);
        Assert.Equal(.125f, controller.Frame!.Value.Progress, 5);
        controller.Update(motion, 100, Route, 2, Lead, Capability, 1, true);
        Assert.Equal(.125f, controller.Frame!.Value.Progress, 5);
        controller.Update(motion, 100, Route, 2, Lead, Capability, 1.1, true);
        Assert.Equal(.25f, controller.Frame!.Value.Progress, 5);
    }

    [Fact]
    public void ShortClipsNeverExceedQuarterProgressPerCollisionValidation()
    {
        var cap = Capability with { Duration = .3f };
        var controller = Controller((previous, next) =>
        {
            Assert.InRange(next.Progress - previous.Progress, 0, .25f);
            return GuideJumpEvidence.Clear;
        });
        var motion = Start(controller, cap);
        controller.Update(motion, 100, Route, 2, Lead, cap, .12, true); controller.ResolvePlacement(motion, true);
        for (var i = 1; i <= 5 && controller.Active; i++)
        { controller.Update(motion, 100, Route, 2, Lead, cap, .12 + i * .1, true); controller.ResolvePlacement(motion, true); }
        Assert.False(controller.Active);
    }

    [Fact]
    public void RejectedTakeoffDoesNotStartAnInvisibleAnimation()
    {
        var controller = Controller(); var motion = new CompanionRouteMotion();
        controller.Update(motion, 100, Route, 2, Lead, Capability, 0, true); controller.ResolvePlacement(motion, true);
        controller.Update(motion, 100, Route, 2, Lead, Capability, .1, true); controller.ResolvePlacement(motion, false);
        Assert.False(controller.Active); Assert.Null(controller.Frame); Assert.Equal(2, motion.Current.Arc);
        controller.Update(motion, 100, Route, 2, Lead, Capability, .2, true);
        Assert.True(controller.Active); Assert.Equal(0, controller.Frame!.Value.Progress);
    }

    [Fact]
    public void OnlyAcceptedCompletePlacementEndsTheJump()
    {
        var controller = Controller(); var motion = Start(controller); var end = controller.Frame!.Value.Plan.Request.EndArc;
        double time = .1;
        while (true)
        {
            time += .05;
            controller.Update(motion, 100, Route, 2, Lead, Capability, time, true);
            if (controller.Frame!.Value.Complete) break;
            controller.ResolvePlacement(motion, true);
            Assert.True(time < 2);
        }
        Assert.True(controller.Active);
        controller.ResolvePlacement(motion, false);
        Assert.True(controller.Active); Assert.False(controller.Frame!.Value.Complete); Assert.True(motion.Current.Arc < end);
        controller.Update(motion, 100, Route, 2, Lead, Capability, time + .05, true);
        Assert.True(controller.Frame!.Value.Complete);
        controller.ResolvePlacement(motion, true);
        Assert.False(controller.Active); Assert.Null(controller.Frame); Assert.Equal(end, motion.Current.Arc);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void AcceptedTraversalTracksAuthoredDurationAcrossFrameRates(int fps)
    {
        var controller = Controller(); var motion = Start(controller); var plan = controller.Frame!.Value.Plan;
        var step = 0;
        while (controller.Active)
        {
            Assert.True(++step <= fps * 2);
            controller.Update(motion, 100, Route, 90, Lead, Capability, .1 + step / (double)fps, true);
            controller.ResolvePlacement(motion, true);
        }
        Assert.InRange(step / (double)fps, Capability.Duration - .0001, Capability.Duration + 1d / fps + .0001);
        Assert.Equal(plan.Request.EndArc, motion.Current.Arc);
    }

    [Fact]
    public void CollisionExceptionAndInvalidClockCannotAdvanceOrDestroyAcceptedJump()
    {
        var unavailable = true;
        var controller = Controller((_, _) => unavailable ? throw new InvalidOperationException("loading") : GuideJumpEvidence.Clear);
        var motion = Start(controller); var before = controller.Frame;
        controller.Update(motion, 100, Route, 2, Lead, Capability, .2, true);
        Assert.Equal(before, controller.Frame); Assert.Equal(2, motion.Current.Arc);
        controller.Update(motion, 100, Route, 2, Lead, Capability, double.NaN, true);
        Assert.Equal(before, controller.Frame); Assert.True(controller.Active);
        unavailable = false;
        controller.Update(motion, 100, Route, 2, Lead, Capability, .3, true);
        Assert.Equal(.125f, controller.Frame!.Value.Progress, 5);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(101, true, false)]
    [InlineData(100, false, false)]
    [InlineData(100, true, true)]
    public void LifecycleOrCapabilityChangeCancelsWithoutInventingAGroundStep(uint zone, bool hasRoute, bool changeModel)
    {
        var controller = Controller(); var motion = Start(controller); var before = motion.Current.Position;
        controller.Update(motion, zone, Route, 90, Lead, changeModel ? Capability with { AnimationId = 1 } : Capability, .2, hasRoute);
        Assert.False(controller.Active); Assert.Null(controller.Frame); Assert.Equal(before, motion.Current.Position);
        controller.Reset(); motion.Reset(); Assert.False(motion.Current.HasPosition);
    }

    [Fact]
    public void ApprovalForAnotherArcCannotSnapTheCompanionToItsTakeoff()
    {
        var controller = new CompanionJumpTraversal((_, route, arc, direction, cap, _) =>
            new(GuideJumpEvidence.Approved, Plan(route, arc + 1, direction, cap), "stale"), (_, _) => GuideJumpEvidence.Clear);
        var motion = new CompanionRouteMotion();
        controller.Update(motion, 100, Route, 2, Lead, Capability, 0, true); controller.ResolvePlacement(motion, true);
        controller.Update(motion, 100, Route, 2, Lead, Capability, .1, true);
        Assert.False(controller.Active); Assert.Equal(2, motion.Current.Arc);
    }

    [Fact]
    public void MalformedApprovedPlanIsRejectedBeforeSamplingAnInvalidPose()
    {
        var controller = new CompanionJumpTraversal((_, route, arc, direction, cap, _) =>
            new(GuideJumpEvidence.Approved, Plan(route, arc, direction, cap) with { Apex = float.NaN }, "malformed"),
            (_, _) => GuideJumpEvidence.Clear);
        var motion = new CompanionRouteMotion();
        controller.Update(motion, 100, Route, 2, Lead, Capability, 0, true); controller.ResolvePlacement(motion, true);
        controller.Update(motion, 100, Route, 2, Lead, Capability, .1, true);
        Assert.False(controller.Active); Assert.Null(controller.Frame); Assert.Equal(2, motion.Current.Arc);
    }

    [Fact]
    public void BackwardReturningCompanionProbesItsOwnNegativeTravelDirection()
    {
        var seen = 0;
        var controller = new CompanionJumpTraversal((_, route, arc, direction, cap, _) =>
        { seen = direction; return new(GuideJumpEvidence.Approved, Plan(route, arc, direction, cap), "approved"); }, (_, _) => GuideJumpEvidence.Clear);
        var motion = new CompanionRouteMotion();
        motion.Update(Route, 5, Lead, 0, true); motion.ResolvePlacement(true);
        var returning = new CompanionIntentFrame(CompanionIntentMode.Returning, 1, 0, true);
        controller.Update(motion, 100, Route, 0, returning, Capability, .1, true); controller.ResolvePlacement(motion, true);
        Assert.Equal(-1, seen); Assert.True(controller.Active);
        controller.Update(motion, 100, Route, 0, returning, Capability, .2, true);
        Assert.True(motion.Current.Arc < 5);
    }
}
