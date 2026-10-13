using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>The actual actor hitbox, not an inferred cosmetic body radius.
/// A conservative horizontal exclusion applies to the complete approved guide
/// envelope at every airborne pose as well as the ground landing.</summary>
public readonly record struct GuideBoardingAvatarExclusion(GuideBoardingIdentity Identity, Vector3 Position,
    float Radius, double SampledAt)
{
    public const float Clearance = .08f;
    public bool Clear(GuideBoardingFrame frame, double now) => Identity == frame.Identity
        && GuideBoardingGeometry.Fresh(SampledAt, now) && GuideBoardingGeometry.Finite(Position)
        && float.IsFinite(Radius) && Radius is >= .01f and <= 5
        && Heading.FlatDistance(frame.Position, Position) >= Radius + frame.Capability.Radius + Clearance;
}

/// <summary>
/// A supported ground rendezvous synchronized to an actually observed Teleport.
/// Does not inspect or claim an attachment to the game's visual-effect object.
/// The input path must be the complete, current, validated navmesh query result;
/// this consumer uses its real final vertex and never appends a target bridge.
/// Full trajectory/world body sweeps are STILL required by the controller and
/// exact-pose preflight before any airborne or held rendering can be accepted.
/// </summary>
public static class GuideTeleportGroundReceiver
{
    public const float FootLift = .03f;
    private static readonly int[] FootprintIndices = [0,1,2,0,2,3];

    public static bool TryCreate(GuideBoardingIdentity identity, GuideBoardingCapability capability,
        bool casting, byte actionType, uint actionId, float elapsed, float total,
        Vector3 player, float actualHitboxRadius, ReadOnlySpan<Vector3> confirmedNavmeshPath,
        TrailSurfaceHit floor, Quaternion guideRotation, ulong observationSequence,
        double sampledAt, double now, out GuideBoardingReceiverVolume receiver)
    {
        receiver = default;
        if (!identity.Valid || !capability.Valid || observationSequence == 0
            || !GuideBoardingGeometry.Fresh(sampledAt, now) || !GuideBoardingGeometry.Unit(guideRotation)
            || !TeleportCastObservation.TryRemaining(casting,actionType,actionId,elapsed,total,out var remaining) || remaining <= 0
            || confirmedNavmeshPath.Length is < 2 or > 4096) return false;
        foreach (var point in confirmedNavmeshPath) if (!GuideBoardingGeometry.Finite(point)) return false;
        var end = confirmedNavmeshPath[^1];
        if (Heading.FlatDistance(end,floor.Point) > .02f || Math.Abs(end.Y-floor.Point.Y) > .05f) return false;
        var at = floor.Point + Vector3.UnitY * FootLift;
        var r = capability.Radius;
        Span<Vector3> footprint = stackalloc Vector3[4]
        { at + new Vector3(-r,0,-r), at + new Vector3(r,0,-r), at + new Vector3(r,0,r), at + new Vector3(-r,0,r) };
        // This small concrete producer requires one triangle covering the
        // entire conservative footprint. Seams/slopes needing extra support
        // queries decline here; no extrapolated plane or ground snap is used.
        if (!GuideModelSupport.TryGetLift(footprint,FootprintIndices,floor,FootLift,out var lift) || lift > .0001f) return false;
        var anchor = new GuideBoardingAnchor(GuideBoardingTarget.TeleportGround,GuideBoardingAnchorSource.ObservedTeleportGround,
            identity.PlayerGeneration,identity.CastSequence,at,guideRotation,r,capability.Height,sampledAt);
        var avatar = new GuideBoardingAvatarExclusion(identity,player,actualHitboxRadius,sampledAt);
        // No trajectory is authorized by this temporary envelope-only frame.
        var frame = new GuideBoardingFrame(identity,capability,GuideBoardingPhase.Holding,at,guideRotation,
            capability.HoldingClip,0,0,null!);
        if (!anchor.Fits(capability,now) || !avatar.Clear(frame,now)) return false;
        receiver = new(identity,anchor,observationSequence,at+Vector3.UnitY*(capability.Height*.5f),
            Quaternion.Identity,new(r+.0001f,capability.Height*.5f+.0001f,r+.0001f),avatar);
        return true;
    }
}
