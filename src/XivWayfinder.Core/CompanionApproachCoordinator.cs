using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>
/// A rendezvous target arc on an actual queried route. RebaseArc=0 requires an
/// explicit position-preserving adoption; an unspawned guide must first seed at
/// zero, not at ProgressArc. HoldPosition never authorizes an ordinary move.
/// </summary>
public readonly record struct CompanionApproachSelection(Vector3[] Route, float ProgressArc,
    float? RebaseArc, bool HoldPosition, bool Ready, bool AtDestination, bool QueryPending = false);

/// <summary>
/// Query-only, framework-thread routing to a supplied rendezvous. It does not
/// steer the player or manufacture missing route endpoints. Cancellation keeps
/// the single query slot occupied until even an uncooperative provider finishes.
/// </summary>
public sealed class CompanionApproachCoordinator(
    Func<Vector3, Vector3, CancellationToken, Task<IReadOnlyList<Vector3>>> query) : IDisposable
{
    public const double MinRequestInterval = 2;
    public const double CorralRequestInterval = .75;
    public const double RequestTimeout = 15;
    public const float OriginTolerance = .002f;
    public const float DestinationHorizontalTolerance = .5f;
    public const float DestinationVerticalTolerance = .5f;
    public const float RetargetDistance = .25f;
    private sealed record Pending(long Epoch, uint Zone, Vector3 Start, Vector3 Goal, bool HadPosition,
        double Started, CancellationTokenSource Cancel, Task<IReadOnlyList<Vector3>> Work)
    { public bool Abandoned { get; set; } }
    private sealed record ReadyRoute(Vector3[] Route, Vector3[] Snapshot, Vector3 Start, bool HadPosition, float Length);
    private Pending? pending;
    private ReadyRoute? active;
    private Vector3? destination;
    private Vector3? lastObservedGoal;
    private uint territory;
    private long epoch;
    private double lastRequest = double.NegativeInfinity, lastTime = double.NaN;
    private double lastFailedRequest = double.NegativeInfinity;
    private bool disposed;
    public string Status { get; private set; } = "No companion rendezvous requested.";
    public bool QueryInFlight => pending is not null;

    public CompanionApproachSelection Select(uint zone, Vector3 rendezvous, Vector3 player,
        CompanionRouteMotionFrame accepted, double now, bool enabled) =>
        Select(zone, rendezvous, player, accepted, now, enabled, MinRequestInterval);

    /// <summary>Moving-player rendezvous only; failures retain the normal two-second retry bound.</summary>
    public CompanionApproachSelection SelectCorral(uint zone, Vector3 rendezvous, Vector3 player,
        CompanionRouteMotionFrame accepted, double now, bool enabled) =>
        Select(zone, rendezvous, player, accepted, now, enabled, CorralRequestInterval);

    private CompanionApproachSelection Select(uint zone, Vector3 rendezvous, Vector3 player,
        CompanionRouteMotionFrame accepted, double now, bool enabled, double interval)
    {
        if (disposed) return Hold(accepted);
        if (!enabled || zone == 0 || !double.IsFinite(now) || !GuideConnectorPlan.Point(rendezvous)
            || !GuideConnectorPlan.Point(player) || !ValidOrigin(accepted))
        {
            Invalidate(); ObserveAbandoned();
            Status = "Rendezvous unavailable; holding the last accepted position.";
            return Hold(accepted);
        }
        var origin = accepted.HasPosition ? accepted.Position : player;
        var reversed = now < lastRequest || !double.IsNaN(lastTime) && now < lastTime;
        if (territory != zone || reversed)
        {
            Invalidate(); destination = lastObservedGoal = null; territory = zone;
            if (reversed)
            {
                lastRequest = now;
                lastFailedRequest = now; // A backwards clock cannot burst retries.
            }
        }
        lastTime = now;
        var freshGoal = lastObservedGoal is not { } observed || Vector3.Distance(observed, rendezvous) > .05f;
        if (freshGoal) lastObservedGoal = rendezvous;
        if (destination is null) destination = rendezvous;
        else if (Vector3.Distance(destination.Value, rendezvous) > RetargetDistance
            && now - lastRequest >= interval
            // Corral intent already publishes discrete fresh targets. Polling
            // a queued old cue at cooldown expiry permanently offsets these
            // two clocks and chases where the runner was half a second ago.
            && (interval == MinRequestInterval || active is null || freshGoal))
        {
            Invalidate(); destination = rendezvous;
        }
        // Small target jitter, and a large change inside the retarget cooldown,
        // retain the existing confirmed route. A query itself always holds.
        ObserveResult(origin, accepted.HasPosition, now);
        if (SelectActive(origin, accepted) is { } selected) return selected;
        if (pending is not null || now - lastRequest < interval
            || now - lastFailedRequest < MinRequestInterval) return Hold(accepted);

        lastRequest = now;
        var cancellation = new CancellationTokenSource();
        try
        {
            var work = query(origin, destination.Value, cancellation.Token)
                ?? throw new InvalidOperationException("Missing rendezvous query task.");
            pending = new(epoch, zone, origin, destination.Value, accepted.HasPosition, now, cancellation, work);
            Status = "Finding a walkable route to the companion's rendezvous.";
        }
        catch (Exception)
        {
            cancellation.Dispose();
            lastFailedRequest = lastRequest;
            Status = "Rendezvous query unavailable; holding the last accepted position.";
        }
        // A synchronous provider can return before an unspawned player's
        // position changes. Asynchronous results still revalidate that origin.
        ObserveResult(origin, accepted.HasPosition, now);
        return SelectActive(origin, accepted) ?? Hold(accepted);
    }

    private void ObserveResult(Vector3 origin, bool hasPosition, double now)
    {
        if (pending is not { } request) return;
        if (!request.Abandoned && (now - request.Started >= RequestTimeout
            || hasPosition != request.HadPosition || Vector3.Distance(origin, request.Start) > OriginTolerance))
            Cancel(request);
        if (!request.Work.IsCompleted) return;
        pending = null;
        try
        {
            if (!request.Abandoned && request.Epoch == epoch && request.Zone == territory
                && request.Goal == destination && request.Work.IsCompletedSuccessfully
                && TryRoute(request.Start, request.Goal, request.Work.Result, out var route))
            {
                active = new(route, (Vector3[])route.Clone(), request.Start, request.HadPosition, PathTrail.Length(route));
                Status = "Following the companion's validated rendezvous route.";
            }
            else
            {
                _ = request.Work.Exception;
                if (!request.Abandoned) lastFailedRequest = request.Started;
                Status = request.Abandoned ? "Rendezvous query cancelled; waiting to retry."
                    : "No complete walkable route to the rendezvous was returned.";
            }
        }
        catch (Exception)
        {
            if (!request.Abandoned) lastFailedRequest = request.Started;
            // Provider collections can also fail or mutate while being copied.
            Status = "Rendezvous route could not be validated; holding position.";
        }
        finally { request.Cancel.Dispose(); }
    }

    private CompanionApproachSelection? SelectActive(Vector3 origin, CompanionRouteMotionFrame accepted)
    {
        if (active is not { } route) return null;
        if (!route.Route.AsSpan().SequenceEqual(route.Snapshot))
        { active = null; Status = "Rendezvous route changed; requesting fresh evidence."; return null; }
        var adopted = accepted.HasPosition && ReferenceEquals(accepted.Route, route.Route);
        if (!adopted && (accepted.HasPosition != route.HadPosition || Vector3.Distance(origin, route.Start) > OriginTolerance))
        { active = null; Status = "Guide moved before route adoption; requesting a matching origin."; return null; }
        var arrived = adopted && route.Length - accepted.Arc <= .002f
            && Vector3.Distance(accepted.Position, route.Route[^1]) <= OriginTolerance;
        return new(route.Route, route.Length, adopted ? null : 0, arrived, true, arrived);
    }

    private static bool ValidOrigin(CompanionRouteMotionFrame accepted)
    {
        if (!accepted.HasPosition) return true;
        // Existing independently owned routes use the motion controller's
        // larger bound, not the 512-point limit on this new approach query.
        if (accepted.Route is not { Length: >= 2 and <= 4096 } || !GuideConnectorPlan.Point(accepted.Position)
            || !float.IsFinite(accepted.Arc) || accepted.Arc < 0) return false;
        foreach (var point in accepted.Route) if (!GuideConnectorPlan.Point(point)) return false;
        var length = PathTrail.Length(accepted.Route);
        return float.IsFinite(length) && length > .001f && accepted.Arc <= length
            && Vector3.Distance(PathTrail.At(accepted.Route, accepted.Arc), accepted.Position) <= OriginTolerance;
    }

    private CompanionApproachSelection Hold(CompanionRouteMotionFrame accepted) =>
        new(accepted.Route ?? [], accepted.Arc, null, true, false, false,
            pending is { Abandoned: false, Work.IsCompleted: false });

    private static bool TryRoute(Vector3 start, Vector3 goal, IReadOnlyList<Vector3>? returned, out Vector3[] route)
    {
        route = [];
        if (returned is null || returned.Count is < 2 or > GuideConnectorPlan.MaxPoints) return false;
        var copy = new Vector3[returned.Count];
        for (var i = 0; i < copy.Length; ++i) copy[i] = returned[i];
        if (!GuideConnectorPlan.Valid(copy) || PathTrail.Length(copy) > GuideConnectorPlan.MaxConnectorLength
            || Vector3.Distance(start, copy[0]) > OriginTolerance
            || Heading.FlatDistance(goal, copy[^1]) > DestinationHorizontalTolerance
            || Math.Abs(goal.Y - copy[^1].Y) > DestinationVerticalTolerance) return false;
        copy[0] = start; // Numerical roundoff only. No appended/prepended bridge.
        if (!GuideConnectorPlan.Valid(copy) || PathTrail.Length(copy) > GuideConnectorPlan.MaxConnectorLength) return false;
        route = copy; // Keep the actual returned endpoint inside the arrival region.
        return true;
    }

    public void Reset()
    {
        Invalidate(); destination = lastObservedGoal = null; territory = 0; lastTime = double.NaN;
        ObserveAbandoned();
        Status = "No companion rendezvous requested.";
    }

    private void Invalidate()
    {
        epoch++; active = null;
        if (pending is { } request) Cancel(request);
    }

    private void ObserveAbandoned()
    {
        if (pending is not { Work.IsCompleted: true } request) return;
        _ = request.Work.Exception; request.Cancel.Dispose(); pending = null;
    }

    private static void Cancel(Pending request)
    {
        if (request.Abandoned) return;
        request.Abandoned = true;
        try { request.Cancel.Cancel(); } catch (AggregateException) { /* Provider callbacks cannot interrupt the framework. */ }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Reset();
        if (pending is not { } request) return;
        pending = null;
        // Only managed request state is retained; no game/native pointers.
        _ = request.Work.ContinueWith(task => { _ = task.Exception; request.Cancel.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
