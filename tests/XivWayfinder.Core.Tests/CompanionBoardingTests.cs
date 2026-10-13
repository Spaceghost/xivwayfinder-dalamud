using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionBoardingTests
{
    private static readonly GuideBoardingIdentity Identity = new(100, 10, 20, 1);
    private static readonly GuideBoardingCapability Capability = new(GuideJumpAnimationKind.ImportedClip,
        "synthetic verified test fixture, not a shipped model", 3, 4, 5, .15f, .5f, 1, GuideBoardingHoldMode.Loop);

    private static GuideBoardingObservation Input(double time) => new(Identity, true, true, 1, 5, (float)time, 30, false,
        new([Vector3.Zero, new(10, 0, 0)], 0, Vector3.Zero, Quaternion.Identity, true, true, time),
        new(GuideBoardingTarget.Arms, GuideBoardingAnchorSource.ObservedReceivingPose, 10, 30,
            new(1, 1, 0), Quaternion.Identity, .25f, .7f, time), Capability, true);

    private static GuideBoardingTrajectory Path(GuideBoardingRequest request) => new(
        request.Returning ? request.Capability.ReturningClip : request.Capability.BoardingClip,
        [new(0, request.Start, request.StartRotation, 0),
            new(.4f, Vector3.Lerp(request.Start, request.End, .5f) + Vector3.UnitY * .2f,
                Quaternion.Slerp(request.StartRotation, request.EndRotation, .5f), .4f),
            new(.8f, request.End, request.EndRotation, .8f)]);

    private sealed class Rig
    {
        public readonly List<GuideBoardingRequest> Requests = [];
        public readonly List<(GuideBoardingFrame Before, GuideBoardingFrame After)> Sweeps = [];
        public GuideJumpEvidence Preparation = GuideJumpEvidence.Approved;
        public GuideJumpEvidence Sweep = GuideJumpEvidence.Clear;
        public Func<GuideBoardingRequest, double, GuideBoardingProof>? PrepareOverride;
        public CompanionBoardingController Controller { get; }
        public Rig()
        {
            Controller = new((request, now) =>
            {
                Requests.Add(request);
                return PrepareOverride?.Invoke(request, now) ?? new(Preparation, Path(request), now);
            }, (before, after) => { Sweeps.Add((before, after)); return Sweep; });
        }
        public void Start()
        {
            var proposed = Controller.Update(Input(0), 0);
            Assert.True(proposed.PlacementPending); Assert.True(proposed.BlocksRoute);
            Controller.ResolvePlacement(true);
        }
        public GuideBoardingUpdate Step(double time, bool accepted = true, bool casting = true)
        {
            var result = Controller.Update(Input(time) with { Casting = casting }, time);
            return result.PlacementPending ? Controller.ResolvePlacement(accepted) : result;
        }
    }

    [Fact]
    public void AwaitingGpuSubmissionKeepsProposalAndConsumesTimeWithoutBankingMotion()
    {
        var rig = new Rig(); rig.Start();
        var proposal = rig.Controller.Update(Input(.02), .02);
        Assert.True(proposal.PlacementPending);
        rig.Controller.HoldClock(5);
        Assert.Equal(proposal.Frame, rig.Controller.Frame);
        var submitted = rig.Controller.ResolvePlacement(true);
        Assert.Equal(proposal.Frame, submitted.Frame);
        var next = rig.Controller.Update(Input(5.01), 5.01);
        Assert.True(next.PlacementPending);
        Assert.InRange(next.Frame!.Value.TrajectoryTime - proposal.Frame!.Value.TrajectoryTime, .0099f, .0101f);
    }

    [Fact]
    public void CancelledUnsubmittedProposalRecoversFromLastGpuAcknowledgedPose()
    {
        var rig = new Rig(); rig.Start(); var last = rig.Step(.02).Frame!.Value;
        var unseen = rig.Controller.Update(Input(.08), .08);
        Assert.True(unseen.Frame!.Value.TrajectoryTime > last.TrajectoryTime);
        rig.Controller.HoldClock(.09); rig.Controller.ResolvePlacement(false);
        var recovery = rig.Controller.Update(Input(.10) with { Casting = false }, .10);
        Assert.True(recovery.PlacementPending);
        Assert.Equal(last.Position, rig.Requests[^1].Start);
        Assert.Equal(last, rig.Requests[^1].From);
        Assert.Equal(last.Position, recovery.Frame!.Value.Position);
    }

    [Theory]
    [InlineData(false, 1, 5)]
    [InlineData(true, 1, 6)]
    [InlineData(true, 5, 5)]
    [InlineData(true, 5, 7)]
    public void OnlyObservedActualTeleportMayInitiateBoarding(bool casting, byte type, uint action)
    {
        var rig = new Rig();
        var result = rig.Controller.Update(Input(0) with { Casting = casting, CastActionType = type, CastActionId = action }, 0);
        Assert.Empty(rig.Requests); Assert.False(result.BlocksRoute); Assert.False(result.Hide); Assert.Null(result.Frame);
    }

    [Theory]
    [InlineData(0)] // no renderer-accepted placement
    [InlineData(1)] // unverified walkability
    [InlineData(2)] // stale approach
    [InlineData(3)] // missing receiver evidence
    [InlineData(4)] // stale receiver
    [InlineData(5)] // larger than arms allow
    [InlineData(6)] // no separately verified authored boarding capability
    [InlineData(7)] // guide has not actually approached the receiver
    [InlineData(8)] // another player's anchor
    [InlineData(9)] // no observed cast identity
    [InlineData(10)] // invalid root rotation
    [InlineData(11)] // receiver's free volume is too small
    [InlineData(12)] // no receiving pose may stand in for an observed VFX instance
    [InlineData(13)] // seated/standing-up actor cannot start a ground takeoff
    public void MissingEvidenceDoesNotCaptureOrHideTheExistingGuide(int missing)
    {
        var rig = new Rig(); var input = Input(1);
        input = missing switch
        {
            0 => input with { Approach = input.Approach with { Accepted = false } },
            1 => input with { Approach = input.Approach with { Walkable = false } },
            2 => input with { Approach = input.Approach with { SampledAt = .8 } },
            3 => input with { Anchor = input.Anchor with { Source = GuideBoardingAnchorSource.Unknown } },
            4 => input with { Anchor = input.Anchor with { SampledAt = .8 } },
            5 => input with { Capability = Capability with { Height = 1 } },
            6 => input with { Capability = default },
            7 => input with { Anchor = input.Anchor with { Position = new(4, 1, 0) } },
            8 => input with { Anchor = input.Anchor with { PlayerGeneration = 11 } },
            9 => input with { Identity = Identity with { CastSequence = 0 } },
            10 => input with { Approach = input.Approach with { Rotation = default } },
            11 => input with { Anchor = input.Anchor with { Radius = .1f } },
            12 => input with { Anchor = input.Anchor with { Target = GuideBoardingTarget.TeleportEffect } },
            _ => input with { CanStartTransfer = false },
        };
        var result = rig.Controller.Update(input, 1);
        Assert.Empty(rig.Requests); Assert.False(result.BlocksRoute); Assert.False(result.Hide);
    }

    [Fact]
    public void AnActualEffectAnchorIsAnExplicitProofNotInferredFromTheCastOrPlayerRoot()
    {
        var rig = new Rig(); var input = Input(0);
        input = input with
        {
            Anchor = input.Anchor with
            {
                Target = GuideBoardingTarget.TeleportEffect,
                Source = GuideBoardingAnchorSource.ObservedCastEffect,
                Height = 2
            },
            Capability = Capability with { Height = 1.2f }
        };
        Assert.True(rig.Controller.Update(input, 0).PlacementPending);
        Assert.Equal(GuideBoardingTarget.TeleportEffect, Assert.Single(rig.Requests).Anchor.Target);
    }

    [Fact]
    public void OwnedGroundJumpIsUntouchedAndBoardingWaitsUntilItsAcceptedLanding()
    {
        var rig = new Rig();
        for (var i = 0; i < 10; i++)
        {
            var result = rig.Controller.Update(Input(i * .1) with { GroundJumpActive = true }, i * .1);
            Assert.False(result.BlocksRoute); Assert.Null(result.Frame); Assert.False(result.Hide);
        }
        Assert.Empty(rig.Requests);
        Assert.True(rig.Controller.Update(Input(1), 1).PlacementPending);
    }

    [Theory]
    [InlineData(GuideJumpEvidence.Unknown)]
    [InlineData(GuideJumpEvidence.Pending)]
    [InlineData(GuideJumpEvidence.Clear)]
    [InlineData(GuideJumpEvidence.Blocked)]
    public void OnlyFullTrajectoryApprovalCanAcquireVisualOwnership(GuideJumpEvidence evidence)
    {
        var rig = new Rig { Preparation = evidence };
        var result = rig.Controller.Update(Input(0), 0);
        Assert.False(result.BlocksRoute); Assert.False(result.Hide); Assert.Null(result.Frame);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StaleOrMismatchedApprovalNeverMovesTheGuide(int invalid)
    {
        var rig = new Rig
        {
            PrepareOverride = (request, now) => invalid switch
        {
            0 => new(GuideJumpEvidence.Approved, Path(request), now - .11),
            1 => new(GuideJumpEvidence.Approved, Path(request with { Start = request.Start + Vector3.UnitX }), now),
            2 => new(GuideJumpEvidence.Approved, Path(request with { End = request.End + Vector3.UnitX }), now),
            _ => new(GuideJumpEvidence.Approved, Path(request with { Returning = true }), now),
        }
        };
        Assert.False(rig.Controller.Update(Input(1), 1).BlocksRoute);
    }

    [Fact]
    public void RefusedTakeoffReleasesRouteWithoutRepeatingTheSameCast()
    {
        var rig = new Rig(); rig.Controller.Update(Input(0), 0);
        var result = rig.Controller.ResolvePlacement(false);
        Assert.False(result.BlocksRoute); Assert.Null(result.Frame); Assert.False(result.Hide);
        rig.Controller.Update(Input(.1), .1);
        Assert.Single(rig.Requests);
        Assert.True(rig.Controller.Update(Input(.2) with { Identity = Identity with { CastSequence = 2 } }, .2).PlacementPending);
        rig.Controller.ResolvePlacement(false);
        Assert.False(rig.Controller.Update(Input(.3), .3).BlocksRoute); // stale older cast
    }

    [Fact]
    public void CastMustHaveTimeForTheActualVerifiedTransferNotJustBeCastingThisFrame()
    {
        var rig = new Rig();
        var result = rig.Controller.Update(Input(0) with { CastElapsed = 4.8f, CastTotal = 5 }, 0);
        Assert.False(result.BlocksRoute); Assert.Null(result.Frame); Assert.False(result.Hide);
    }

    [Fact]
    public void RejectedOrUnacknowledgedStepsRestoreRootRotationAndClipWithoutTimeBanking()
    {
        var rig = new Rig(); rig.Start(); rig.Step(.1);
        var accepted = rig.Controller.Frame;
        rig.Step(.2, false); Assert.Equal(accepted, rig.Controller.Frame);
        rig.Controller.Update(Input(.3), .3); // not acknowledged
        rig.Controller.Update(Input(.4), .4);
        Assert.Equal(.2f, rig.Controller.Frame!.Value.TrajectoryTime, 5);
        rig.Controller.ResolvePlacement(false); Assert.Equal(accepted, rig.Controller.Frame);
    }

    [Theory]
    [InlineData(GuideJumpEvidence.Unknown)]
    [InlineData(GuideJumpEvidence.Pending)]
    [InlineData(GuideJumpEvidence.Blocked)]
    public void EveryBodyOrAuthoredPoseRefusalKeepsTheAcceptedVisibleFrame(GuideJumpEvidence evidence)
    {
        var rig = new Rig(); rig.Start(); rig.Step(.1); var accepted = rig.Controller.Frame;
        rig.Sweep = evidence;
        for (var i = 2; i <= 30; i++)
        {
            var result = rig.Step(i * .1);
            Assert.Equal(accepted, result.Frame); Assert.True(result.BlocksRoute); Assert.False(result.Hide);
        }
        rig.Sweep = GuideJumpEvidence.Clear; rig.Step(3.1);
        Assert.Equal(.2f, rig.Controller.Frame!.Value.TrajectoryTime, 5);
    }

    [Fact]
    public void CancellationFromAirbornePoseRequestsNewSupportedReturnAndOnlyHandsOffAfterAcceptedLanding()
    {
        var rig = new Rig(); rig.Start(); rig.Step(.1); rig.Step(.2);
        var before = rig.Controller.Frame!.Value;
        var recovery = rig.Controller.Update(Input(.3) with { Casting = false, Identity = Identity with { CastSequence = 0 } }, .3);
        var request = rig.Requests[^1];
        Assert.True(request.Returning); Assert.Equal(before, request.From);
        Assert.Equal(before.Position, request.Start); Assert.Equal(Vector3.Zero, request.End);
        Assert.Equal(before.Position, recovery.Frame!.Value.Position); Assert.True(recovery.BlocksRoute);
        rig.Controller.ResolvePlacement(true);
        GuideBoardingUpdate result = default;
        for (var i = 4; i <= 13; i++)
        {
            result = rig.Controller.Update(Input(i * .1) with { Casting = false }, i * .1);
            if (result.Frame?.Phase == GuideBoardingPhase.Landed)
            {
                Assert.True(result.BlocksRoute); Assert.Null(result.GroundReturn);
                rig.Controller.ResolvePlacement(false); Assert.True(rig.Controller.Active);
                result = rig.Controller.Update(Input(i * .1 + .1) with { Casting = false }, i * .1 + .1);
                result = rig.Controller.ResolvePlacement(true); break;
            }
            rig.Controller.ResolvePlacement(true);
        }
        Assert.False(result.BlocksRoute); Assert.False(result.Hide);
        Assert.Equal(Vector3.Zero, result.Frame!.Value.Position);
        Assert.Equal(0, result.GroundReturn!.Arc);
        Assert.Equal(new[] { Vector3.Zero, new Vector3(10, 0, 0) }, result.GroundReturn.Route.ToArray());
        var copy = result.GroundReturn.CopyRoute(); copy[1] = new(100, 100, 100);
        Assert.Equal(new(10, 0, 0), result.GroundReturn.Route[1]);
        Assert.True(result.GroundReturn.SampledAt >= 1.1);
    }

    [Fact]
    public void ImmediateCancellationCanUseVerifiedStationaryLandingInsteadOfStrandingAtTakeoff()
    {
        var rig = new Rig(); rig.Start();
        rig.PrepareOverride = (request, now) => new(GuideJumpEvidence.Approved,
            new(request.Capability.ReturningClip,
                [new(0, request.Start, request.StartRotation, 0), new(.3f, request.End, request.EndRotation, .3f)]), now);
        var result = rig.Step(.1, casting: false);
        Assert.Equal(GuideBoardingPhase.Returning, result.Frame!.Value.Phase);
        for (var i = 2; i <= 7 && result.BlocksRoute; i++) result = rig.Step(i * .1, casting: false);
        Assert.False(result.BlocksRoute); Assert.NotNull(result.GroundReturn);
        Assert.Equal(Vector3.Zero, result.Frame!.Value.Position);
    }

    [Fact]
    public void ANewCastDoesNotRetargetAnAlreadyAirborneGuideAndTakeoffRouteIsDefensivelyCopied()
    {
        var rig = new Rig(); var input = Input(0); var sourceRoute = input.Approach.Route;
        rig.Controller.Update(input, 0); rig.Controller.ResolvePlacement(true);
        sourceRoute[1] = new(100, 50, 50);
        rig.Step(.1);
        var before = rig.Controller.Frame;
        var result = rig.Controller.Update(Input(.2) with { Identity = Identity with { CastSequence = 2 } }, .2);
        Assert.True(rig.Requests[^1].Returning); Assert.Equal(Identity, rig.Requests[^1].Identity);
        Assert.Equal(before!.Value.Position, result.Frame!.Value.Position);
        rig.Controller.ResolvePlacement(true);
        for (var i = 3; i < 15 && result.BlocksRoute; i++) result = rig.Step(i * .1, casting: false);
        Assert.Equal(new Vector3(10, 0, 0), result.GroundReturn!.Route[1]);
    }

    [Fact]
    public void RefusedRecoveryTransitionRestoresTheOriginalTrajectoryAndRetriesFromTheAcceptedPose()
    {
        var rig = new Rig(); rig.Start(); rig.Step(.1); rig.Step(.2); var before = rig.Controller.Frame;
        rig.Step(.3, false, false); Assert.Equal(before, rig.Controller.Frame);
        var result = rig.Controller.Update(Input(.6) with { Casting = false }, .6);
        Assert.Equal(before!.Value.Position, result.Frame!.Value.Position);
        Assert.Equal(0, result.Frame.Value.TrajectoryTime);
        Assert.Equal(before, rig.Requests[^1].From);
        Assert.True(result.PlacementPending);
    }

    [Fact]
    public void MissingRecoveryEvidenceIsVisibleExplicitRecoveryWorkNotSilentRouteResumeOrGroundSnap()
    {
        var rig = new Rig(); rig.Start(); rig.Step(.1); var before = rig.Controller.Frame;
        rig.Preparation = GuideJumpEvidence.Unknown;
        var result = rig.Step(.2, casting: false);
        Assert.True(result.NeedsRecovery); Assert.True(result.BlocksRoute); Assert.False(result.Hide);
        Assert.Equal(before, result.Frame); Assert.Null(result.GroundReturn);
        rig.Preparation = GuideJumpEvidence.Approved;
        Assert.Equal(GuideBoardingPhase.Returning, rig.Step(.5, casting: false).Frame!.Value.Phase);
    }

    [Fact]
    public void MissingOrMovedReceiverTriggersRecoveryNotAnUnprovedMidflightAttachment()
    {
        var rig = new Rig(); rig.Start(); rig.Step(.1); var before = rig.Controller.Frame;
        var input = Input(.2);
        var result = rig.Controller.Update(input with { Anchor = input.Anchor with { Position = new(2, 1, 0) } }, .2);
        Assert.True(rig.Requests[^1].Returning); Assert.Equal(before!.Value.Position, result.Frame!.Value.Position);
    }

    [Fact]
    public void HoldingUsesDistinctVerifiedClipAndOnlySweptSmallActualAnchorMotion()
    {
        var rig = new Rig(); rig.Start();
        for (var i = 1; i <= 10; i++) rig.Step(i * .1);
        Assert.Equal(GuideBoardingPhase.Holding, rig.Controller.Frame!.Value.Phase);
        Assert.Equal(Capability.HoldingClip, rig.Controller.Frame.Value.Clip);
        var input = Input(1.1);
        input = input with { Anchor = input.Anchor with { Position = input.Anchor.Position + new Vector3(.01f, 0, 0) } };
        var result = rig.Controller.Update(input, 1.1);
        Assert.True(result.PlacementPending); Assert.Equal(input.Anchor.Position, rig.Sweeps[^1].After.Position);
        rig.Controller.ResolvePlacement(false);
        Assert.Equal(Input(0).Anchor.Position, rig.Controller.Frame.Value.Position);
    }

    [Fact]
    public void BoardingEndpointDoesNotClaimAHoldingPoseUntilThatDistinctPoseIsAccepted()
    {
        var rig = new Rig(); rig.Start();
        var time = 0d;
        while (rig.Controller.Frame!.Value.TrajectoryTime < .8f)
        { time += .1; rig.Step(time); Assert.True(time < 2); }
        var endpoint = rig.Controller.Frame;
        Assert.Equal(GuideBoardingPhase.Boarding, endpoint!.Value.Phase);
        Assert.Equal(Capability.BoardingClip, endpoint.Value.Clip);
        var hold = rig.Controller.Update(Input(time + .1), time + .1);
        Assert.Equal(GuideBoardingPhase.Holding, hold.Frame!.Value.Phase);
        Assert.Equal(Capability.HoldingClip, hold.Frame.Value.Clip);
        Assert.Equal(0, hold.Frame.Value.ClipTime);
        rig.Controller.ResolvePlacement(false);
        Assert.Equal(endpoint, rig.Controller.Frame);
        rig.Step(time + .2);
        Assert.Equal(GuideBoardingPhase.Holding, rig.Controller.Frame!.Value.Phase);
    }

    [Theory]
    [InlineData(GuideBoardingHoldMode.Loop)]
    [InlineData(GuideBoardingHoldMode.FixedPose)]
    public void HoldingTimeFollowsVerifiedDurationAndLoopOrFixedPoseContract(GuideBoardingHoldMode mode)
    {
        var rig = new Rig(); var capability = Capability with { HoldingDuration = .7f, HoldingMode = mode, HoldingPoseTime = .3f };
        for (var i = 0; i <= 250; i++)
        {
            var time = i * .1;
            var result = rig.Controller.Update(Input(time) with { Capability = capability }, time);
            if (result.Frame is { Phase: GuideBoardingPhase.Holding } frame)
            {
                Assert.InRange(frame.ClipTime, 0, .6999999f);
                if (mode == GuideBoardingHoldMode.FixedPose) Assert.Equal(.3f, frame.ClipTime);
            }
            rig.Controller.ResolvePlacement(true);
        }
        Assert.Equal(GuideBoardingPhase.Holding, rig.Controller.Frame!.Value.Phase);
        Assert.True(rig.Sweeps.Count > 200);
    }

    [Theory]
    [InlineData(float.NaN, GuideBoardingHoldMode.Loop, 0)]
    [InlineData(0, GuideBoardingHoldMode.Loop, 0)]
    [InlineData(1, GuideBoardingHoldMode.Unknown, 0)]
    [InlineData(1, GuideBoardingHoldMode.FixedPose, 1)]
    [InlineData(1, GuideBoardingHoldMode.FixedPose, -.1f)]
    public void MissingOrMalformedHoldMetadataCannotAuthorizeBoarding(float duration, GuideBoardingHoldMode mode, float at)
    {
        var rig = new Rig();
        var result = rig.Controller.Update(Input(0) with
        {
            Capability = Capability with
            {
                HoldingDuration = duration,
                HoldingMode = mode,
                HoldingPoseTime = at
            }
        }, 0);
        Assert.Empty(rig.Requests); Assert.False(result.BlocksRoute);
    }

    [Theory]
    [InlineData(.035f, true)]
    [InlineData(.2f, false)]
    [InlineData(.7f, false)]
    public void AcceptedTakeoffAllowsKnownFootLiftButNotAnotherFloorAtTheSameRouteXZ(float height, bool allowed)
    {
        var rig = new Rig(); var input = Input(0);
        var result = rig.Controller.Update(input with { Approach = input.Approach with { Position = new(0, height, 0) } }, 0);
        Assert.Equal(allowed, result.BlocksRoute);
        if (!allowed) Assert.Empty(rig.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LifecycleChangesHideInsteadOfMovingAnOldVisualIntoAnotherZoneOrModel(int change)
    {
        var rig = new Rig(); rig.Start(); rig.Step(.1); var input = Input(.2);
        input = change switch
        {
            0 => input with { Identity = Identity with { Zone = 101 } },
            1 => input with { Identity = Identity with { PlayerGeneration = 11 } },
            2 => input with { Identity = Identity with { GuideGeneration = 21 } },
            _ => input with { Visible = false },
        };
        var result = rig.Controller.Update(input, .2);
        Assert.True(result.Hide); Assert.False(result.BlocksRoute); Assert.Null(result.Frame);
        Assert.Single(rig.Requests);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10)]
    public void BadOrDiscontinuousClocksCannotFastForwardTheAirbornePose(double time)
    {
        var rig = new Rig(); rig.Start(); rig.Step(.1); var before = rig.Controller.Frame;
        var result = rig.Controller.Update(Input(time), time);
        Assert.Equal(before, result.Frame);
    }

    [Fact]
    public void TrajectoryOwnsItsDataAndRejectsMalformedUnboundedOrInstantRootMotion()
    {
        GuideBoardingRootKey[] keys = [new(0, Vector3.Zero, Quaternion.Identity, 0), new(1, Vector3.One, Quaternion.Identity, 1)];
        var path = new GuideBoardingTrajectory(3, keys);
        Assert.True(path.Valid); keys[1] = keys[1] with { Position = new(100, 0, 0) };
        Assert.Equal(Vector3.One, path.End); Assert.Equal(Vector3.One * .5f, path.Sample(.5f).Position);
        Assert.False(new GuideBoardingTrajectory(3, keys).Valid);
        Assert.False(new GuideBoardingTrajectory(3, []).Valid);
        Assert.False(new GuideBoardingTrajectory(3, [keys[0], keys[0]]).Valid);
        Assert.False(new GuideBoardingTrajectory(3, [keys[0], keys[1] with { Position = Vector3.One, Rotation = default }]).Valid);
        Assert.False(new GuideBoardingTrajectory(3, [keys[0], keys[1] with { Position = Vector3.One, ClipTime = float.NaN }]).Valid);
        Assert.Throws<InvalidOperationException>(() => path.Sample(float.NaN));
    }

    [Fact]
    public void ExceptionsInEvidenceProvidersFailClosedWithoutTakingTheGuideAway()
    {
        var rig = new Rig { PrepareOverride = (_, _) => throw new InvalidOperationException("no scene") };
        Assert.False(rig.Controller.Update(Input(0), 0).BlocksRoute);
        Assert.False(rig.Controller.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableOrThrowingPreparationHasBoundedRetriesInsteadOfPerFrameHeavyWork(bool throws)
    {
        var rig = new Rig
        {
            PrepareOverride = (_, now) => throws
            ? throw new InvalidOperationException("no evidence") : new(GuideJumpEvidence.Unknown, null, now)
        };
        for (var i = 0; i < 144; i++)
            Assert.False(rig.Controller.Update(Input(i / 144d), i / 144d).BlocksRoute);
        Assert.InRange(rig.Requests.Count, 3, 4);
        Assert.False(rig.Controller.Active);
    }

    [Fact]
    public void RecoveryRetriesAreBoundedWhileTheLastAcceptedPoseRemainsVisible()
    {
        var rig = new Rig(); rig.Start(); rig.Step(.1); var accepted = rig.Controller.Frame;
        rig.Preparation = GuideJumpEvidence.Unknown;
        for (var i = 0; i < 144; i++)
        {
            var result = rig.Step(.2 + i / 144d, casting: false);
            Assert.Equal(accepted, result.Frame); Assert.True(result.NeedsRecovery); Assert.False(result.Hide);
        }
        Assert.InRange(rig.Requests.Count(request => request.Returning), 3, 4);
    }
}
