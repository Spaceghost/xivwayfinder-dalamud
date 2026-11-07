using System.Numerics;

namespace XivWayfinder.Core;

public enum GuideBoardingTarget { Arms, TeleportEffect, TeleportGround }
public enum GuideBoardingPhase { Boarding, Holding, Returning, Landed }
public enum GuideBoardingAnchorSource { Unknown, ObservedReceivingPose, ObservedCastEffect, ObservedTeleportGround }
public enum GuideBoardingHoldMode { Unknown, Loop, FixedPose }

/// <summary>
/// Opaque, local visual identities, not native pointers or account identifiers.
/// CastSequence increases for each actually observed cast within the actor generation.
/// </summary>
public readonly record struct GuideBoardingIdentity(uint Zone, ulong PlayerGeneration, ulong GuideGeneration, ulong CastSequence)
{
    public bool ActorsValid => Zone != 0 && PlayerGeneration != 0 && GuideGeneration != 0;
    public bool Valid => ActorsValid && CastSequence != 0;
    public bool SameActors(GuideBoardingIdentity other) => Zone == other.Zone
        && PlayerGeneration == other.PlayerGeneration && GuideGeneration == other.GuideGeneration;
}

/// <summary>
/// Produced only after verifying transfer, held and recovery poses AND their
/// whole-clip scaled body envelope. An ordinary Jump/Hop capability is not this
/// proof. Native timeline IDs and imported clip indices have different domains.
/// </summary>
public readonly record struct GuideBoardingCapability(GuideJumpAnimationKind Kind, string EvidenceKey,
    int BoardingClip, int HoldingClip, int ReturningClip, float Radius, float Height,
    float HoldingDuration, GuideBoardingHoldMode HoldingMode, float HoldingPoseTime = 0)
{
    public bool Valid => Kind is GuideJumpAnimationKind.NativeTimeline or GuideJumpAnimationKind.ImportedClip
        && !string.IsNullOrWhiteSpace(EvidenceKey) && EvidenceKey.Length <= 256
        && BoardingClip >= 0 && HoldingClip >= 0 && ReturningClip >= 0
        && (Kind != GuideJumpAnimationKind.NativeTimeline || BoardingClip > 0 && HoldingClip > 0 && ReturningClip > 0)
        && float.IsFinite(Radius) && Radius is >= .03f and <= 2
        && float.IsFinite(Height) && Height is >= .08f and <= 3
        && HoldingMode is GuideBoardingHoldMode.Loop or GuideBoardingHoldMode.FixedPose
        && float.IsFinite(HoldingDuration) && HoldingDuration is >= .05f and <= 120
        && float.IsFinite(HoldingPoseTime) && HoldingPoseTime >= 0 && HoldingPoseTime < HoldingDuration;
}

/// <summary>
/// A measured receiver's approved ROOT placement and free body envelope. Arms
/// require a fresh, synchronized receiving pose and body-fit evidence; a hand
/// midpoint alone is insufficient. Effect anchors require the actual cast VFX
/// instance and volume, never merely the player root or a casting flag.
/// TeleportGround is instead an actual navmesh endpoint with fresh floor and
/// avatar-exclusion evidence: a cast-synchronized ground hop, not VFX attachment.
/// </summary>
public readonly record struct GuideBoardingAnchor(GuideBoardingTarget Target, GuideBoardingAnchorSource Source,
    ulong PlayerGeneration, ulong AnchorGeneration, Vector3 Position, Quaternion Rotation, float Radius, float Height, double SampledAt)
{
    public bool Fits(GuideBoardingCapability body, double now) => body.Valid && PlayerGeneration != 0 && AnchorGeneration != 0
        && GuideBoardingGeometry.Fresh(SampledAt, now) && GuideBoardingGeometry.Finite(Position) && GuideBoardingGeometry.Unit(Rotation)
        && float.IsFinite(Radius) && Radius is > 0 and <= 2
        && float.IsFinite(Height) && Height is > 0 and <= 4 && body.Radius <= Radius && body.Height <= Height
        && (Target == GuideBoardingTarget.Arms && Source == GuideBoardingAnchorSource.ObservedReceivingPose
                && body.Radius <= .3f && body.Height <= .8f
            || Target == GuideBoardingTarget.TeleportEffect && Source == GuideBoardingAnchorSource.ObservedCastEffect && body.Radius <= .6f
            || Target == GuideBoardingTarget.TeleportGround && Source == GuideBoardingAnchorSource.ObservedTeleportGround);
}

/// <summary>Last renderer-accepted root on a real walkable route; never a proposed or guessed placement.</summary>
public readonly record struct GuideBoardingApproach(Vector3[] Route, float Arc, Vector3 Position, Quaternion Rotation,
    bool Accepted, bool Walkable, double SampledAt)
{
    public bool Valid(double now)
    {
        if (!Accepted || !Walkable || !GuideBoardingGeometry.Fresh(SampledAt, now)
            || Route is not { Length: >= 2 and <= 4096 } || !float.IsFinite(Arc) || Arc < 0
            || !GuideBoardingGeometry.Finite(Position) || !GuideBoardingGeometry.Unit(Rotation)) return false;
        foreach (var point in Route) if (!GuideBoardingGeometry.Finite(point)) return false;
        var length = PathTrail.Length(Route);
        if (!float.IsFinite(length) || length <= 0 || Arc > length) return false;
        var at = PathTrail.At(Route, Arc);
        // Allow the established .035 foot lift, not a .7-yalm alias to a
        // different floor at the same XZ. The actual accepted root is retained.
        return Heading.FlatDistance(at, Position) <= .02f && Math.Abs(at.Y - Position.Y) <= .05f;
    }
}

public readonly record struct GuideBoardingObservation(GuideBoardingIdentity Identity, bool Visible,
    bool Casting, byte CastActionType, uint CastActionId, float CastElapsed, float CastTotal,
    bool GroundJumpActive, GuideBoardingApproach Approach, GuideBoardingAnchor Anchor, GuideBoardingCapability Capability,
    bool CanStartTransfer)
{
    public bool RealTeleport => TeleportCastObservation.TryRemaining(Casting, CastActionType, CastActionId,
        CastElapsed, CastTotal, out var remaining) && remaining > 0;
}

public readonly record struct GuideBoardingRequest(GuideBoardingIdentity Identity, GuideBoardingCapability Capability,
    GuideBoardingAnchor Anchor, Vector3 Start, Quaternion StartRotation, Vector3 End, Quaternion EndRotation,
    bool Returning, GuideBoardingFrame? From = null);
public readonly record struct GuideBoardingRootKey(float Time, Vector3 Position, Quaternion Rotation, float ClipTime);

/// <summary>
/// Bounded, defensively copied samples of an authored/verified root trajectory.
/// No default parabola, invented connector or ordinary route arc represents
/// airborne boarding. The producer must sweep the entire sampled trajectory.
/// </summary>
public sealed class GuideBoardingTrajectory
{
    private readonly GuideBoardingRootKey[] keys;
    public int Clip { get; }
    public float Duration => keys.Length == 0 ? 0 : keys[^1].Time;
    public Vector3 Start => keys.Length == 0 ? default : keys[0].Position;
    public Vector3 End => keys.Length == 0 ? default : keys[^1].Position;
    public bool Valid { get; }
    public ReadOnlySpan<GuideBoardingRootKey> Keys => keys;

    public GuideBoardingTrajectory(int clip, ReadOnlySpan<GuideBoardingRootKey> samples)
    {
        Clip = clip;
        keys = samples.Length is >= 2 and <= 128 ? samples.ToArray() : [];
        if (clip < 0 || keys.Length == 0 || keys[0].Time != 0 || Duration is < .2f or > 3 || !float.IsFinite(Duration)) return;
        var length = 0f;
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            if (!float.IsFinite(key.Time) || !float.IsFinite(key.ClipTime) || key.ClipTime is < 0 or > 30
                || !GuideBoardingGeometry.Finite(key.Position) || !GuideBoardingGeometry.Unit(key.Rotation)) return;
            if (i == 0) continue;
            if (key.Time <= keys[i - 1].Time || key.ClipTime < keys[i - 1].ClipTime) return;
            var distance = Vector3.Distance(key.Position, keys[i - 1].Position);
            if (distance / (key.Time - keys[i - 1].Time) > 12) return;
            length += distance;
        }
        Valid = float.IsFinite(length) && length is >= 0 and <= 8;
    }

    public GuideBoardingRootKey Sample(float time)
    {
        if (!Valid || !float.IsFinite(time)) throw new InvalidOperationException("Boarding trajectory is not valid.");
        time = Math.Clamp(time, 0, Duration);
        for (var i = 1; i < keys.Length; i++)
        {
            if (time > keys[i].Time) continue;
            var a = keys[i - 1]; var b = keys[i]; var t = (time - a.Time) / (b.Time - a.Time);
            return new(time, Vector3.Lerp(a.Position, b.Position, t), Quaternion.Slerp(a.Rotation, b.Rotation, t),
                a.ClipTime + (b.ClipTime - a.ClipTime) * t);
        }
        return keys[^1];
    }
}

/// <summary>Approved means full trajectory/body clearance and, for recovery, a fresh supported landing.</summary>
public readonly record struct GuideBoardingProof(GuideJumpEvidence Evidence, GuideBoardingTrajectory? Trajectory, double SampledAt);
public readonly record struct GuideBoardingFrame(GuideBoardingIdentity Identity, GuideBoardingCapability Capability,
    GuideBoardingPhase Phase, Vector3 Position, Quaternion Rotation, int Clip, float ClipTime, float TrajectoryTime,
    GuideBoardingTrajectory Trajectory);

/// <summary>
/// An accepted, freshly supported visual landing at the original route
/// occurrence. CopyRoute supplies geometry for an explicit exact-arc handoff;
/// it is not permission to replace a route integrator's accepted world position.
/// </summary>
public sealed class GuideBoardingLanding
{
    private readonly Vector3[] route;
    public ReadOnlySpan<Vector3> Route => route;
    public Vector3[] CopyRoute() => (Vector3[])route.Clone();
    public float Arc { get; }
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public double SampledAt { get; }
    internal GuideBoardingLanding(GuideBoardingApproach approach, double now)
    {
        route = (Vector3[])approach.Route.Clone(); Arc = approach.Arc; Position = approach.Position;
        Rotation = approach.Rotation; SampledAt = now;
    }
}

public readonly record struct GuideBoardingUpdate(GuideBoardingFrame? Frame, bool PlacementPending,
    bool BlocksRoute, bool Hide, GuideBoardingLanding? GroundReturn, bool NeedsRecovery);

internal static class GuideBoardingGeometry
{
    public static bool Fresh(double sampledAt, double now) => double.IsFinite(sampledAt) && double.IsFinite(now)
        && sampledAt >= 0 && now >= sampledAt && now - sampledAt <= .1;
    public static bool Finite(Vector3 p) => TrailPlacement.IsFinite(p)
        && Math.Abs(p.X) <= 100000 && Math.Abs(p.Y) <= 100000 && Math.Abs(p.Z) <= 100000;
    public static bool Unit(Quaternion q) => float.IsFinite(q.LengthSquared()) && Math.Abs(q.LengthSquared() - 1) <= .001f;
    public static bool SameRotation(Quaternion a, Quaternion b) => Unit(a) && Unit(b) && Math.Abs(Quaternion.Dot(a, b)) >= .99999f;
}
