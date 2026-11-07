using System.Numerics;

namespace XivWayfinder.Core;

public enum GuideJumpAnimationKind { None, NativeTimeline, ImportedClip }
public enum GuideJumpEvidence { Unknown, Pending, Clear, Blocked, Approved }
public enum GuideJumpPhase { Takeoff, Airborne, Landing, Complete }

/// <summary>Authored animation and measured conservative body envelope; absence is not permission to hop.</summary>
public readonly record struct GuideJumpCapability(GuideJumpAnimationKind Kind, int AnimationId,
    string Name, float Duration, float Radius, float Height)
{
    public bool Valid => Kind is GuideJumpAnimationKind.NativeTimeline or GuideJumpAnimationKind.ImportedClip
        && AnimationId >= 0 && (Kind != GuideJumpAnimationKind.NativeTimeline || AnimationId > 0)
        && !string.IsNullOrWhiteSpace(Name) && float.IsFinite(Duration) && Duration is >= .3f and <= 1.8f
        && float.IsFinite(Radius) && Radius is >= .03f and <= .6f
        && float.IsFinite(Height) && Height is >= .08f and <= 3f;
}

public readonly record struct GuideJumpFloor(GuideJumpEvidence Evidence, Vector3 Position, float NormalY);
public readonly record struct GuideJumpFrame(GuideJumpPlan Plan, float Arc, Vector3 GroundPosition,
    Vector3 FeetPosition, GuideJumpPhase Phase, float Progress, float ClipTime)
{
    public bool Complete => Phase == GuideJumpPhase.Complete;
}

/// <summary>An immutable request confined to one original path segment. No synthesized connector.</summary>
public sealed record GuideJumpRequest(Vector3[] Route, int Segment, float StartArc, float EndArc,
    Vector3 Start, Vector3 End, GuideJumpCapability Capability)
{
    public const int FloorSampleCount = 17;
    public Vector3 Point(int index) => Vector3.Lerp(Start, End, index / (float)(FloorSampleCount - 1));
    public bool MatchesRoute() => Route is { Length: >= 2 and <= 4096 } && Capability.Valid
        && float.IsFinite(StartArc) && float.IsFinite(EndArc) && StartArc >= 0 && EndArc >= 0
        && Math.Abs(EndArc - StartArc) is >= 1.399f and <= 2.201f
        && TrailPlacement.IsFinite(Start) && TrailPlacement.IsFinite(End)
        && Segment >= 0 && Segment + 1 < Route.Length
        && Vector3.Distance(PathTrail.At(Route, StartArc), Start) <= .0001f
        && Vector3.Distance(PathTrail.At(Route, EndArc), End) <= .0001f
        && GuideJumpPolicy.OnOneSegment(Route, Segment, StartArc, EndArc);
}

/// <summary>Geometry-approved kinematic foot path synchronized to one authored jump clip.</summary>
public sealed record GuideJumpPlan(GuideJumpRequest Request, Vector3 Takeoff, Vector3 Landing, float Apex)
{
    public Vector3[] Route => Request.Route;
    public float Duration => Request.Capability.Duration;
    public bool Valid => Request is not null && Request.MatchesRoute()
        && TrailPlacement.IsFinite(Takeoff) && TrailPlacement.IsFinite(Landing)
        && Heading.FlatDistance(Takeoff, Request.Start) <= .02f
        && Heading.FlatDistance(Landing, Request.End) <= .02f
        && Math.Abs(Takeoff.Y - Request.Start.Y) <= .7f && Math.Abs(Landing.Y - Request.End.Y) <= .7f
        && Math.Abs(Takeoff.Y - Landing.Y) <= .2f && float.IsFinite(Apex) && Apex is > 0 and <= GuideJumpPolicy.MaximumApex;
    public float FlightProgress(float progress) => Request.Capability.Kind == GuideJumpAnimationKind.NativeTimeline
        ? Math.Clamp(progress * Duration / GuideNativeJumpSet.FlightSeconds, 0, 1) : Math.Clamp(progress, 0, 1);
    public GuideJumpFrame Sample(float progress)
    {
        if (!Valid) throw new InvalidOperationException("Jump geometry or body evidence changed.");
        if (!float.IsFinite(progress)) throw new ArgumentOutOfRangeException(nameof(progress));
        progress = Math.Clamp(progress, 0, 1);
        var flight = FlightProgress(progress);
        var arc = Request.StartArc + (Request.EndArc - Request.StartArc) * flight;
        var ground = PathTrail.At(Route, arc);
        // XZ comes from the same route occurrence, never a player/goal chord.
        var feet = ground with
        {
            Y = Takeoff.Y + (Landing.Y - Takeoff.Y) * flight
            + 4 * Apex * flight * (1 - flight) + GuideJumpPolicy.FootClearance
        };
        var phase = progress >= 1 ? GuideJumpPhase.Complete
            : Request.Capability.Kind == GuideJumpAnimationKind.NativeTimeline
                ? progress * Duration < GuideNativeJumpSet.TakeoffSeconds ? GuideJumpPhase.Takeoff
                    : flight >= 1 ? GuideJumpPhase.Landing : GuideJumpPhase.Airborne
                : progress < .12f ? GuideJumpPhase.Takeoff : progress > .85f ? GuideJumpPhase.Landing : GuideJumpPhase.Airborne;
        return new(this, arc, ground, feet, phase, progress, progress * Duration);
    }
}

public readonly record struct GuideJumpProbeResult(GuideJumpEvidence Evidence, GuideJumpPlan? Plan, string Reason);

/// <summary>Only evidenced low obstacles on an existing straight route leg are eligible. Missing floors are not jumps.</summary>
public static class GuideJumpPolicy
{
    public const float FootClearance = .035f;
    public const float MaximumApex = .95f;
    public const float MinimumObstacle = .10f;
    public const float MaximumObstacle = .5f;

    public static bool TryRequest(Vector3[] route, float arc, int direction, GuideJumpCapability capability,
        out GuideJumpRequest? request)
    {
        request = null;
        if (!capability.Valid || route is null || route.Length is < 2 or > 4096
            || !float.IsFinite(arc) || arc < 0 || direction is not (-1 or 1)) return false;
        foreach (var p in route) if (!TrailPlacement.IsFinite(p)) return false;
        var total = PathTrail.Length(route);
        if (!float.IsFinite(total) || arc > total) return false;
        var distance = Math.Clamp(capability.Radius * 4 + 1, 1.4f, 2.2f);
        var end = arc + direction * distance;
        if (end < 0 || end > total) return false;
        var offset = 0f;
        for (var i = 0; i + 1 < route.Length; ++i)
        {
            var length = Heading.FlatDistance(route[i], route[i + 1]);
            if (length <= .0001f) { offset += length; continue; }
            var low = Math.Min(arc, end); var high = Math.Max(arc, end);
            if (low >= offset && high <= offset + length)
            {
                var start = PathTrail.At(route, arc); var finish = PathTrail.At(route, end);
                if (Math.Abs(finish.Y - start.Y) > .25f) return false;
                request = new(route, i, arc, end, start, finish, capability);
                return true;
            }
            offset += length;
        }
        return false;
    }

    internal static bool OnOneSegment(Vector3[] route, int segment, float start, float end)
    {
        var at = 0f;
        for (var i = 0; i < segment; ++i) at += Heading.FlatDistance(route[i], route[i + 1]);
        var length = Heading.FlatDistance(route[segment], route[segment + 1]);
        return float.IsFinite(at + length) && Math.Min(start, end) >= at && Math.Max(start, end) <= at + length;
    }

    public static GuideJumpProbeResult Evaluate(GuideJumpRequest request, ReadOnlySpan<GuideJumpFloor> floors)
    {
        if (!request.Capability.Valid || !request.MatchesRoute() || floors.Length != GuideJumpRequest.FloorSampleCount)
            return new(GuideJumpEvidence.Unknown, null, "Route or body evidence changed.");
        for (var i = 0; i < floors.Length; ++i)
        {
            var hit = floors[i]; var routePoint = request.Point(i);
            if (hit.Evidence == GuideJumpEvidence.Unknown) return new(GuideJumpEvidence.Unknown, null, "Ground evidence incomplete.");
            if (hit.Evidence != GuideJumpEvidence.Clear || !TrailPlacement.IsFinite(hit.Position)
                || !float.IsFinite(hit.NormalY) || hit.NormalY < .55f
                || Heading.FlatDistance(hit.Position, routePoint) > .02f || Math.Abs(hit.Position.Y - routePoint.Y) > .7f)
                return new(GuideJumpEvidence.Blocked, null, "No continuous, matching walkable floor.");
        }
        var takeoff = floors[0].Position; var landing = floors[^1].Position;
        if (Math.Abs(takeoff.Y - landing.Y) > .2f)
            return new(GuideJumpEvidence.Blocked, null, "Landing is not on the same floor.");
        var peak = 0f; var apex = 0f;
        for (var i = 1; i + 1 < floors.Length; ++i)
        {
            var t = i / (float)(floors.Length - 1);
            var obstacle = floors[i].Position.Y - (takeoff.Y + (landing.Y - takeoff.Y) * t);
            if (obstacle < -.06f || obstacle > MaximumObstacle)
                return new(GuideJumpEvidence.Blocked, null, "Gap or oversized obstacle.");
            if (obstacle > .06f && (i < 4 || i > floors.Length - 5))
                return new(GuideJumpEvidence.Blocked, null, "No clear takeoff or landing margin.");
            peak = Math.Max(peak, obstacle);
            if (obstacle >= MinimumObstacle)
                apex = Math.Max(apex, (obstacle + request.Capability.Radius * 1.12f + .06f) / (4 * t * (1 - t)));
        }
        if (peak < MinimumObstacle) return new(GuideJumpEvidence.Clear, null, "No obstacle to jump.");
        if (!float.IsFinite(apex) || apex > MaximumApex)
            return new(GuideJumpEvidence.Blocked, null, "Obstacle exceeds this body's low-hop envelope.");
        // This is only a candidate. Runtime must additionally sweep its body and
        // validate support across the footprint before returning Approved.
        return new(GuideJumpEvidence.Unknown, new(request, takeoff, landing, apex), "Awaiting body clearance.");
    }
}
