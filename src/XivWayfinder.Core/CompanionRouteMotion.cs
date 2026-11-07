using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>Actual guide position on Route, which can remain the old route while a safe connector is requested.</summary>
public readonly record struct CompanionRouteMotionFrame(Vector3[] Route, float Arc, float Velocity,
    Vector3 Position, bool HasPosition, bool NeedsConnection);

/// <summary>
/// A client visual walks independently on a confirmed polyline: accelerated arc
/// motion and a lead distance band, never a fixed player-relative world offset.
/// Every emitted position samples Route. Repaths preserve world position within
/// .25 yalms or hold the old pose and request a real navmesh connection. They do
/// not teleport the guide or fabricate a straight connector around an obstacle.
/// Reset only for a zone/lifecycle reset that may legitimately respawn the visual.
/// </summary>
public sealed class CompanionRouteMotion
{
    public const float MaximumSpeed = 9f;
    public const float Acceleration = 8f;
    public const float RebaseTolerance = .25f;
    public const double MaximumQueryPause = .15;
    private Vector3[] route = [];
    private float arc, velocity, length;
    private double previousTime = double.NaN;
    private bool advancing;
    private Vector3[]? speedRoute;
    private float previousPlayerArc, playerSpeed;
    private CompanionRouteMotionFrame placementCheckpoint;
    private float placementLength;
    private bool placementPending;
    private GuideJumpPlan? jumpPlan, placementJumpPlan;
    private float jumpProgress, placementJumpProgress;
    private Vector2 queryMomentum;
    private double queryStarted = double.NaN, queryUpdated = double.NaN;
    public CompanionRouteMotionFrame Current { get; private set; } = new([], 0, 0, default, false, false);

    public void Reset()
    {
        route = []; arc = velocity = length = 0; previousTime = double.NaN; advancing = false;
        speedRoute = null; previousPlayerArc = playerSpeed = 0;
        placementPending = false;
        placementCheckpoint = default;
        placementLength = 0;
        jumpPlan = placementJumpPlan = null;
        jumpProgress = placementJumpProgress = 0;
        ClearQueryMomentum();
        Current = new([], 0, 0, default, false, false);
    }

    public CompanionRouteMotionFrame Update(Vector3[] path, float progressArc, CompanionIntentFrame intent,
        double timeSeconds, bool hasRoute)
    {
        if (!double.IsFinite(timeSeconds)) { ClearQueryMomentum(); return Current; }
        // Placement is synchronous on the framework thread. An unacknowledged
        // prior sample is accepted by default for pure/non-native consumers.
        placementCheckpoint = Current;
        placementLength = length;
        placementPending = true;
        placementJumpPlan = jumpPlan;
        placementJumpProgress = jumpProgress;
        jumpPlan = null;
        jumpProgress = 0;
        var elapsed = double.IsNaN(previousTime) ? 0 : timeSeconds - previousTime;
        previousTime = timeSeconds;
        // Zero retains each mode's established cruise. A caller can request a
        // bounded catch-up pace without changing route ownership or position.
        // Bad hints must stop, not silently authorize the fastest speed.
        if (!float.IsFinite(intent.CruiseSpeed)
            || intent.CruiseSpeed != 0 && (intent.CruiseSpeed is < 1 or > MaximumSpeed))
            return Hold(Current.NeedsConnection);
        if (!hasRoute || !Valid(path, out var nextLength) || !float.IsFinite(progressArc)
            || !float.IsFinite(intent.EffectiveLead)) return Hold(route.Length > 0);
        progressArc = Math.Clamp(progressArc, 0, nextLength);
        RememberPlayerSpeed(path, progressArc, elapsed);
        if (route.Length == 0)
        {
            route = path; length = nextLength; arc = progressArc; velocity = 0; advancing = false;
            return Frame(false);
        }
        if (!ReferenceEquals(route, path))
        {
            var oldPosition = PathTrail.At(route, arc);
            var mappedArc = PathTrail.Project(path, oldPosition, out _);
            var mappedPosition = PathTrail.At(path, mappedArc);
            if (Vector3.Distance(oldPosition, mappedPosition) > RebaseTolerance) return Hold(true);
            var oldTravel = Tangent(route, arc) * velocity;
            route = path; length = nextLength; arc = mappedArc;
            velocity = Vector2.Dot(oldTravel, Tangent(route, arc));
            advancing = false;
        }
        if (elapsed < 0) ClearQueryMomentum();
        if (elapsed <= 0) return Frame(false);
        if (elapsed > .5) return Hold(false); // no catch-up leap after loading/a suspended UI
        if (!double.IsNaN(queryStarted))
        {
            // No paused time becomes travel. Only the current frame advances,
            // with decayed momentum projected onto the actual returned route.
            velocity = Vector2.Dot(QueryMomentumAt(timeSeconds), Tangent(route, arc));
            ClearQueryMomentum();
        }
        var remaining = Math.Min(elapsed, .1);
        var desiredLead = Math.Clamp(intent.EffectiveLead, 1, 16);
        while (remaining > 1e-8)
        {
            var dt = (float)Math.Min(remaining, 1.0 / 120);
            remaining -= dt;
            float wanted;
            if (intent.Mode == CompanionIntentMode.Lead)
            {
                var separation = arc - progressArc;
                if (separation < Math.Max(1, desiredLead - 1)) advancing = true;
                if (separation >= desiredLead + 3 || arc >= length) advancing = false;
                var goal = Math.Min(length, progressArc + desiredLead + 3);
                // Independent lead/wait motion still needs to overtake a
                // steady runner. A cap equal to player speed can never recover
                // the lead band after starting behind. This adjusts cruise,
                // not position: acceleration and distance-band stops remain.
                var cruise = intent.CruiseSpeed != 0 ? intent.CruiseSpeed
                    : Math.Clamp(playerSpeed + 1.5f, 3.5f, MaximumSpeed);
                wanted = advancing ? BrakingSpeed(goal - arc, cruise) : 0;
            }
            else
            {
                advancing = false;
                var goal = Math.Min(length, progressArc + desiredLead);
                var cruise = intent.CruiseSpeed != 0 ? intent.CruiseSpeed
                    : intent.Mode == CompanionIntentMode.Returning ? 4.5f : 3f;
                wanted = BrakingSpeed(goal - arc, cruise);
            }
            var nextVelocity = velocity + Math.Clamp(wanted - velocity, -Acceleration * dt, Acceleration * dt);
            var nextArc = arc + (velocity + nextVelocity) * .5f * dt;
            // Endpoints are actual navmesh endpoints, not an invented continuation.
            if (nextArc < 0 || nextArc > length) { nextArc = Math.Clamp(nextArc, 0, length); nextVelocity = 0; }
            arc = nextArc; velocity = nextVelocity;
        }
        return Frame(false);
    }

    /// <summary>
    /// A native floor/wall test may refuse the proposed visual step. Restore
    /// exactly the preceding route occurrence, not a nearest-point projection,
    /// and stop its velocity. The clock and observed player motion stay consumed
    /// so repeated refusals cannot accumulate a later catch-up jump.
    /// </summary>
    public CompanionRouteMotionFrame ResolvePlacement(bool accepted)
    {
        if (!placementPending) return Current;
        placementPending = false;
        if (accepted) return Current;
        ClearQueryMomentum();
        // Update may itself have held the old route because a new one needs a
        // connector. A renderer refusing that held pose must not erase the
        // request, otherwise each frame could suppress its own connector.
        var needsConnection = Current.NeedsConnection || placementCheckpoint.NeedsConnection;
        jumpPlan = placementJumpPlan;
        jumpProgress = placementJumpProgress;
        route = placementCheckpoint.HasPosition ? placementCheckpoint.Route : [];
        arc = placementCheckpoint.HasPosition ? placementCheckpoint.Arc : 0;
        length = placementCheckpoint.HasPosition ? placementLength : 0;
        velocity = 0;
        advancing = false;
        return Frame(placementCheckpoint.HasPosition && needsConnection);
    }

    /// <summary>
    /// Advance only an exact, forward-in-time sample of the active validated
    /// jump on this already-owned route. The caller must validate collision
    /// before applying and resolve renderer placement afterward. Rejection
    /// restores both arc and jump progress; cancellation leaves the actual arc.
    /// </summary>
    public bool TryApplyJumpFrame(GuideJumpFrame frame, double now)
    {
        var plan = frame.Plan;
        if (plan is null || !plan.Valid
            || !ReferenceEquals(route, plan.Route) || !Current.HasPosition || Current.NeedsConnection
            || !double.IsFinite(now) || !float.IsFinite(frame.Progress) || frame.Progress is < 0 or > 1
            || frame != plan.Sample(frame.Progress)) return false;
        var elapsed = double.IsNaN(previousTime) ? 0 : now - previousTime;
        if (elapsed < 0 || elapsed > .5) return false;
        if (!ReferenceEquals(jumpPlan, plan))
        {
            if (frame.Progress != 0 || Math.Abs(arc - plan.Request.StartArc) > .0001f) return false;
        }
        else if (frame.Progress < jumpProgress || frame.Progress - jumpProgress > Math.Min(elapsed, .1) / plan.Duration + .0001)
            return false;
        placementCheckpoint = Current;
        placementLength = length;
        placementPending = true;
        placementJumpPlan = jumpPlan;
        placementJumpProgress = jumpProgress;
        ClearQueryMomentum();
        velocity = elapsed > 0 ? (float)((frame.Arc - arc) / elapsed) : 0;
        arc = frame.Arc;
        jumpPlan = plan;
        jumpProgress = frame.Progress;
        advancing = false;
        previousTime = now;
        Frame(false);
        return true;
    }

    /// <summary>
    /// Consume a blocked, backwards or suspended jump interval without moving
    /// its accepted route occurrence or losing jump ownership. Otherwise a
    /// paused interval over .5s would make every subsequent sample fail forever.
    /// An unacknowledged proposal is rejected, never accepted implicitly.
    /// </summary>
    public CompanionRouteMotionFrame HoldJumpClock(double now)
    {
        ClearQueryMomentum();
        if (!double.IsFinite(now)) return Current;
        if (placementPending) ResolvePlacement(false);
        previousTime = now;
        velocity = 0; advancing = false;
        return Frame(Current.NeedsConnection);
    }

    /// <summary>
    /// Freeze the exact accepted query origin. A short, positively pending
    /// query may retain decaying private tangent momentum, never displayed
    /// velocity or elapsed travel. All other holds must use HoldJumpClock.
    /// </summary>
    public CompanionRouteMotionFrame HoldQueryClock(double now)
    {
        if (placementPending) ResolvePlacement(false);
        if (!double.IsFinite(now)) return Hold(Current.NeedsConnection);
        var elapsed = now - previousTime;
        if (!Current.HasPosition || Current.NeedsConnection || jumpPlan is not null
            || !double.IsFinite(elapsed) || elapsed < 0 || elapsed > .5)
            return HoldJumpClock(now);
        if (double.IsNaN(queryStarted))
        {
            queryStarted = now;
            queryUpdated = previousTime;
            queryMomentum = Tangent(route, arc) * Math.Clamp(velocity, -MaximumSpeed, MaximumSpeed);
        }
        queryMomentum = QueryMomentumAt(now);
        queryUpdated = previousTime = now;
        velocity = 0; advancing = false;
        return Frame(Current.NeedsConnection);
    }

    /// <summary>
    /// Adopt an explicitly known connector/main-route arc without a global
    /// projection that could choose another branch at a self-crossing.
    /// Position, velocity direction and the update clock are preserved; an
    /// unsafe request leaves all state untouched.
    /// </summary>
    public bool TryRebase(Vector3[] path, float knownArc, float tolerance = .002f)
    {
        if (route.Length == 0 || !Valid(path, out var nextLength) || !float.IsFinite(knownArc)
            || knownArc < 0 || knownArc > nextLength || !float.IsFinite(tolerance)
            || tolerance < 0 || tolerance > RebaseTolerance) return false;
        if (Vector3.Distance(PathTrail.At(route, arc), PathTrail.At(path, knownArc)) > tolerance) return false;
        var pausedQuery = !double.IsNaN(queryStarted);
        var oldTravel = pausedQuery ? queryMomentum : Tangent(route, arc) * velocity;
        route = path; length = nextLength; arc = knownArc;
        var tangent = Tangent(route, arc);
        var projected = Vector2.Dot(oldTravel, tangent);
        if (pausedQuery) { queryMomentum = tangent * projected; velocity = 0; }
        else velocity = projected;
        advancing = false;
        speedRoute = null;
        playerSpeed = 0;
        placementPending = false;
        jumpPlan = null;
        jumpProgress = 0;
        Frame(false);
        return true;
    }

    private void RememberPlayerSpeed(Vector3[] path, float progressArc, double elapsed)
    {
        if (!ReferenceEquals(speedRoute, path) || elapsed < 0 || elapsed > .5)
        {
            speedRoute = path;
            previousPlayerArc = progressArc;
            playerSpeed = 0;
        }
        else if (elapsed > 0)
        {
            var advance = progressArc - previousPlayerArc;
            previousPlayerArc = progressArc;
            // A discontinuity is not evidence that the player is sprinting.
            var measured = Math.Abs(advance) > elapsed * 15 + .5 ? 0
                : Math.Clamp((float)(advance / elapsed), 0, MaximumSpeed);
            playerSpeed += (measured - playerSpeed) * (1 - MathF.Exp(-(float)elapsed / .18f));
        }
    }

    private CompanionRouteMotionFrame Hold(bool needsConnection)
    {
        ClearQueryMomentum();
        velocity = 0; advancing = false;
        return Frame(needsConnection);
    }

    private Vector2 QueryMomentumAt(double now)
    {
        if (!double.IsFinite(now) || now < queryUpdated || now - queryStarted > MaximumQueryPause + 1e-9)
            return default;
        var speed = queryMomentum.Length();
        if (!float.IsFinite(speed) || speed <= .0001f) return default;
        var remaining = Math.Max(0, speed - Acceleration * (float)(now - queryUpdated));
        return queryMomentum * (remaining / speed);
    }

    private void ClearQueryMomentum()
    {
        queryMomentum = default;
        queryStarted = queryUpdated = double.NaN;
    }

    private CompanionRouteMotionFrame Frame(bool needsConnection)
    {
        Current = new(route, arc, velocity, route.Length > 0 ? PathTrail.At(route, arc) : default,
            route.Length > 0, needsConnection);
        return Current;
    }

    private static float BrakingSpeed(float distance, float limit)
    {
        if (Math.Abs(distance) < .001f) return 0;
        return Math.Sign(distance) * Math.Min(limit, MathF.Sqrt(2 * Acceleration * Math.Abs(distance)));
    }

    private static Vector2 Tangent(ReadOnlySpan<Vector3> path, float at)
    {
        var offset = 0f;
        for (var i = 0; i + 1 < path.Length; i++)
        {
            var d = new Vector2(path[i + 1].X - path[i].X, path[i + 1].Z - path[i].Z);
            var segment = d.Length();
            if (segment < 1e-4f) continue;
            if (offset + segment > at || i == path.Length - 2) return d / segment;
            offset += segment;
        }
        return Vector2.Zero;
    }

    private static bool Valid(Vector3[]? path, out float total)
    {
        total = 0;
        if (path is null || path.Length < 2) return false;
        foreach (var p in path) if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) return false;
        total = PathTrail.Length(path);
        return float.IsFinite(total) && total > .001f;
    }
}
