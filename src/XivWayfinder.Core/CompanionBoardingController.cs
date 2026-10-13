using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>
/// Visual-only boarding ownership. It cannot cast, move a player or mutate the
/// ground-route/jump controllers. Every root AND authored pose proposal needs a
/// renderer acknowledgement. Cancellation requires a newly evidenced landing,
/// not a rewind, ground snap or invisible return to normal route motion.
/// The validator must cover the exact intervening Trajectory samples, sampled
/// body/pose (including a holding loop's wrap), and fresh supported footprint
/// for a proposed Landed frame. CanStartTransfer is false until an authored
/// stand-up or other prerequisite transition has completed.
/// Runtime bone/effect observation, verified boarding clips and renderer
/// adapters are separate requirements; this policy alone renders no boarding.
/// </summary>
public sealed class CompanionBoardingController(
    Func<GuideBoardingRequest, double, GuideBoardingProof> prepare,
    Func<GuideBoardingFrame, GuideBoardingFrame, GuideJumpEvidence> validate)
{
    public const double PrepareRetrySeconds = .25;
    private GuideBoardingIdentity identity;
    private GuideBoardingIdentity? consumed;
    private GuideBoardingCapability capability;
    private GuideBoardingApproach takeoff;
    private GuideBoardingAnchor anchor;
    private GuideBoardingTrajectory? trajectory;
    private GuideBoardingTrajectory? checkpointTrajectory;
    private GuideBoardingFrame? accepted;
    private GuideBoardingLanding? landing;
    private double previousTime = double.NaN;
    private double nextPreparation = double.NaN;
    private GuideBoardingRequest? previousPreparation;
    private bool pending, returning, recoveryNeeded, hidden;
    private bool checkpointReturning, checkpointRecovery;
    public GuideBoardingFrame? Frame { get; private set; }
    public bool Active => trajectory is not null;
    public string Status { get; private set; } = "No boarding visual.";

    /// <summary>Consume elapsed time while an exact preflighted receipt awaits
    /// asynchronous GPU submission. Do not call Update (which refuses abandoned
    /// proposals) or advance clip/root state until that receipt is resolved.</summary>
    public void HoldClock(double now)
    { previousTime = double.IsFinite(now) && now >= 0 ? now : double.NaN; }

    /// <summary>Only for a hidden model/zone lifecycle, never to abort a visible airborne visual.</summary>
    public void Reset()
    {
        identity = default; capability = default; takeoff = default; anchor = default;
        trajectory = checkpointTrajectory = null; Frame = accepted = null; landing = null; previousTime = double.NaN;
        previousPreparation = null; nextPreparation = double.NaN;
        pending = returning = recoveryNeeded = hidden = false;
        checkpointReturning = checkpointRecovery = false;
        Status = "No boarding visual.";
        // Preserve the consumed cast across a hide/show of this controller.
    }

    public GuideBoardingUpdate Update(GuideBoardingObservation input, double now)
    {
        if (pending) ResolvePlacement(false);
        hidden = false;
        if (!input.Visible || !input.Identity.ActorsValid || Active && !identity.SameActors(input.Identity))
        {
            Reset(); hidden = true; Status = "Boarding hidden after player, guide or zone availability changed.";
            return Result();
        }
        if (!double.IsFinite(now) || now < 0)
        { previousTime = nextPreparation = double.NaN; Status = "Boarding is waiting for a valid clock."; return Result(); }
        var elapsed = double.IsNaN(previousTime) ? 0 : now - previousTime;
        previousTime = now;
        // Do not interrupt, rebase or even query a boarding path while an
        // already-owned ground jump is still being placed by its own controller.
        if (input.GroundJumpActive)
        { Status = "Waiting for the owned ground jump to land."; return Result(); }
        if (elapsed < 0)
        {
            nextPreparation = now + PrepareRetrySeconds;
            Status = "Holding boarding through a clock discontinuity."; return Result();
        }
        if (!Active) return Start(input, now);
        if (input.Capability != capability)
        {
            Reset(); hidden = true; Status = "Boarding hidden because authored model capability changed.";
            return Result();
        }
        if (accepted is not { } before) return Result();
        Frame = before;
        if (!returning && (!input.RealTeleport || input.Identity != identity || !MatchingAnchor(input.Anchor, now)))
            recoveryNeeded = true;
        if (recoveryNeeded) return StartRecovery(now);
        if (elapsed <= 0 || elapsed > .5)
        { Status = "Holding boarding through a clock discontinuity."; return Result(); }

        var dt = (float)Math.Min(Math.Min(elapsed, .1), trajectory!.Duration * .24999);
        if (AtReceiver(before))
        {
            // A real receiver may move slightly as its authored casting pose
            // changes. Large/unobserved movement needs recovery, never a snap.
            if (Vector3.Distance(before.Position, input.Anchor.Position) > Math.Min(.2f, .02f + dt * 3))
            { recoveryNeeded = true; return StartRecovery(now); }
            if (Math.Abs(Quaternion.Dot(before.Rotation, input.Anchor.Rotation)) < .995f)
            { recoveryNeeded = true; return StartRecovery(now); }
            var clipTime = capability.HoldingPoseTime;
            if (before.Phase == GuideBoardingPhase.Holding && capability.HoldingMode == GuideBoardingHoldMode.Loop)
                clipTime = (before.ClipTime + dt) % capability.HoldingDuration;
            Propose(before with
            {
                Phase = GuideBoardingPhase.Holding,
                Clip = capability.HoldingClip,
                Position = input.Anchor.Position,
                Rotation = input.Anchor.Rotation,
                ClipTime = clipTime
            }, before);
            return Result();
        }
        var sample = trajectory!.Sample(Math.Min(trajectory.Duration, before.TrajectoryTime + dt));
        var phase = returning
            ? sample.Time >= trajectory.Duration ? GuideBoardingPhase.Landed : GuideBoardingPhase.Returning
            : GuideBoardingPhase.Boarding;
        // The terminal transfer sample is still the transfer pose. The distinct
        // verified holding pose is requested on the following accepted update.
        var next = new GuideBoardingFrame(identity, capability, phase, sample.Position, sample.Rotation,
            trajectory.Clip, sample.ClipTime, sample.Time, trajectory);
        Propose(next, before);
        return Result();
    }

    private GuideBoardingUpdate Start(GuideBoardingObservation input, double now)
    {
        Frame = null;
        if (!input.Identity.Valid || consumed is { } old && old.SameActors(input.Identity) && input.Identity.CastSequence <= old.CastSequence
            || !input.RealTeleport || !input.Capability.Valid || !input.CanStartTransfer
            || !input.Approach.Valid(now) || !input.Anchor.Fits(input.Capability, now)
            || input.Anchor.PlayerGeneration != input.Identity.PlayerGeneration
            || Heading.FlatDistance(input.Approach.Position, input.Anchor.Position) > 2
            || Math.Abs(input.Approach.Position.Y - input.Anchor.Position.Y) > 2.5f)
        { Status = "Boarding unavailable: waiting for a real cast and verified approach, receiver and authored poses."; return Result(); }
        var request = new GuideBoardingRequest(input.Identity, input.Capability, input.Anchor,
            input.Approach.Position, input.Approach.Rotation, input.Anchor.Position, input.Anchor.Rotation, false);
        if (!TryPrepare(request, now, out var approved)) return Result();
        if (!TeleportCastObservation.TryRemaining(input.Casting, input.CastActionType, input.CastActionId,
                input.CastElapsed, input.CastTotal, out var remaining) || remaining < approved.Duration + .1f)
        { Status = "The real cast is too near departure for this authored boarding transfer."; return Result(); }
        identity = input.Identity; capability = input.Capability; anchor = input.Anchor;
        // Retain the actual validated route geometry, independent of mutable
        // provider arrays. Only this exact old takeoff is a return candidate.
        takeoff = input.Approach with { Route = (Vector3[])input.Approach.Route.Clone() };
        trajectory = approved; landing = null; returning = recoveryNeeded = false;
        var sample = trajectory.Sample(0);
        Frame = new(identity, capability, GuideBoardingPhase.Boarding, sample.Position, sample.Rotation,
            trajectory.Clip, sample.ClipTime, 0, trajectory);
        pending = true;
        Status = "Authored boarding takeoff proposed; awaiting renderer acceptance.";
        return Result();
    }

    private bool MatchingAnchor(GuideBoardingAnchor candidate, double now) => candidate.Fits(capability, now)
        && candidate.PlayerGeneration == identity.PlayerGeneration && candidate.AnchorGeneration == anchor.AnchorGeneration
        && candidate.Target == anchor.Target && candidate.Source == anchor.Source
        // A fixed authored transfer cannot silently retarget its endpoint.
        && (accepted is { } frame && AtReceiver(frame) || Vector3.Distance(candidate.Position, anchor.Position) <= .02f
            && GuideBoardingGeometry.SameRotation(candidate.Rotation, anchor.Rotation));

    private bool AtReceiver(GuideBoardingFrame frame) => !returning &&
        (frame.Phase == GuideBoardingPhase.Holding || frame.Phase == GuideBoardingPhase.Boarding
            && frame.TrajectoryTime >= trajectory!.Duration);

    private GuideBoardingUpdate StartRecovery(double now)
    {
        if (accepted is not { } before) return Result();
        var request = new GuideBoardingRequest(identity, capability, anchor, before.Position, before.Rotation,
            takeoff.Position, takeoff.Rotation, true, before);
        if (!TryPrepare(request, now, out var approved))
        { Status = "Holding the accepted boarding pose until a safe authored return and landing are verified."; return Result(); }
        // Phase switching is also a renderer proposal, not implicit acceptance.
        var sample = approved.Sample(0);
        var next = new GuideBoardingFrame(identity, capability, GuideBoardingPhase.Returning,
            sample.Position, sample.Rotation, approved.Clip, sample.ClipTime, 0, approved);
        if (Propose(next, before))
        { trajectory = approved; returning = true; recoveryNeeded = false; }
        return Result();
    }

    private bool TryPrepare(GuideBoardingRequest request, double now, out GuideBoardingTrajectory approved)
    {
        approved = null!;
        // Full-trajectory collision/animation work may be expensive. Missing
        // evidence and thrown providers use the same bounded retry cadence;
        // a newly cancelled flight may request its first recovery immediately.
        if (previousPreparation is { } previous && previous.Identity == request.Identity
            && previous.Capability == request.Capability && previous.Returning == request.Returning
            && double.IsFinite(nextPreparation) && now < nextPreparation)
        { Status = "Waiting for the bounded boarding evidence retry."; return false; }
        previousPreparation = request;
        nextPreparation = now + PrepareRetrySeconds;
        GuideBoardingProof proof;
        try { proof = prepare(request, now); }
        catch (Exception) { Status = "Boarding evidence is unavailable."; return false; }
        var clip = request.Returning ? request.Capability.ReturningClip : request.Capability.BoardingClip;
        if (proof.Evidence != GuideJumpEvidence.Approved || !GuideBoardingGeometry.Fresh(proof.SampledAt, now)
            || proof.Trajectory is not { Valid: true } path || path.Clip != clip
            || Vector3.Distance(path.Start, request.Start) > .002f || Vector3.Distance(path.End, request.End) > .002f
            || !GuideBoardingGeometry.SameRotation(path.Keys[0].Rotation, request.StartRotation)
            || !GuideBoardingGeometry.SameRotation(path.Keys[^1].Rotation, request.EndRotation))
        { Status = "Waiting for a matching, fresh full-body boarding trajectory."; return false; }
        approved = path;
        return true;
    }

    private bool Propose(GuideBoardingFrame next, GuideBoardingFrame before)
    {
        GuideJumpEvidence evidence;
        try { evidence = validate(before, next); }
        catch (Exception) { evidence = GuideJumpEvidence.Unknown; }
        if (evidence != GuideJumpEvidence.Clear)
        { Frame = before; Status = "Holding boarding until fresh body and authored-pose clearance is available."; return false; }
        checkpointTrajectory = trajectory; checkpointReturning = returning; checkpointRecovery = recoveryNeeded;
        Frame = next; pending = true;
        Status = "Boarding pose proposed; awaiting renderer acceptance.";
        return true;
    }

    public GuideBoardingUpdate ResolvePlacement(bool placed)
    {
        if (!pending) return Result();
        pending = false;
        if (!placed)
        {
            Frame = accepted;
            if (accepted is null)
            { consumed = identity; Reset(); Status = "Boarding takeoff refused; retaining the accepted walkable approach."; }
            else
            {
                trajectory = checkpointTrajectory; returning = checkpointReturning; recoveryNeeded = checkpointRecovery;
                Status = "Boarding placement refused; retaining the accepted root and clip.";
            }
            return Result();
        }
        accepted = Frame; consumed = identity;
        if (accepted is { Phase: GuideBoardingPhase.Landed })
        {
            trajectory = null; landing = new(takeoff, previousTime);
            Status = "Authored return landed at the verified takeoff; route handoff is now allowed.";
        }
        else Status = accepted?.Phase == GuideBoardingPhase.Holding ? "Accepted boarding pose at the verified receiver." : "Boarding pose accepted.";
        return Result();
    }

    private GuideBoardingUpdate Result() => new(Frame, pending, Active, hidden, landing, recoveryNeeded);
}
