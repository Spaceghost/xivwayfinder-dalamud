using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>Choose once, then call CompanionRouteMotion.Update exactly once per frame.</summary>
public readonly record struct GuideRouteSelection(Vector3[] Route, float ProgressArc,
    CompanionIntentFrame Intent, bool Connecting, float? RebaseArc = null);

/// <summary>
/// Main-thread asynchronous query lifecycle. Cancelled work retains the single
/// query slot until completion, so an uncooperative provider cannot cause fan-out.
/// No game objects, character movement gates, timers, downloads or worker sampling.
/// </summary>
public sealed class GuideConnectorCoordinator(
    Func<Vector3, Vector3, CancellationToken, Task<IReadOnlyList<Vector3>>> query) : IDisposable
{
    public const double MinRequestInterval = 2;
    public const double RequestTimeout = 15;
    private sealed record Pending(long Epoch, uint Zone, Vector3[] Source, Vector3[] Snapshot,
        Vector3 Start, Vector3 Join, float JoinArc, double Started,
        CancellationTokenSource Cancel, Task<IReadOnlyList<Vector3>> Work)
    { public bool Abandoned { get; set; } }
    private Pending? pending;
    private GuideConnectorPlan? active;
    private Vector3[]? target;
    private uint territory;
    private long epoch;
    private double lastRequest = double.NegativeInfinity, lastTime = double.NaN;
    private bool disposed;
    public string Status { get; private set; } = "No companion connector needed.";
    public bool QueryInFlight => pending is not null;

    public GuideRouteSelection Select(uint zone, Vector3[] mainRoute, float playerArc,
        CompanionIntentFrame intent, CompanionRouteMotionFrame motion, double now, bool hasRoute)
    {
        var ordinary = new GuideRouteSelection(mainRoute, playerArc, intent, false);
        if (disposed) return ordinary;
        if (!double.IsFinite(now) || !hasRoute || zone == 0 || !GuideConnectorPlan.Valid(mainRoute) ||
            !float.IsFinite(playerArc) || playerArc < 0 || playerArc > PathTrail.Length(mainRoute))
        { Invalidate(); ObserveCompleted(); return ordinary; }
        if (zone != territory || !ReferenceEquals(mainRoute, target) || (!double.IsNaN(lastTime) && now < lastTime))
        {
            var timeReversed = !double.IsNaN(lastTime) && now < lastTime;
            Invalidate(); territory = zone; target = mainRoute;
            if (timeReversed) lastRequest = now; // Restart cooldown, never burst after clock reversal.
        }
        lastTime = now;

        if (pending is { } request)
        {
            if (!request.Abandoned && (now - request.Started >= RequestTimeout || !motion.HasPosition || !motion.NeedsConnection ||
                Vector3.Distance(motion.Position, request.Start) > GuideConnectorPlan.EndpointTolerance ||
                !mainRoute.AsSpan().SequenceEqual(request.Snapshot))) Cancel(request);
            if (request.Work.IsCompleted)
            {
                pending = null;
                if (!request.Abandoned && request.Epoch == epoch && request.Zone == territory &&
                    ReferenceEquals(request.Source, mainRoute) && request.Work.IsCompletedSuccessfully &&
                    GuideConnectorPlan.TryCreate(mainRoute, request.Start, request.JoinArc, request.Work.Result, out var plan))
                { active = plan; Status = "Following the companion's navmesh connector."; }
                else
                {
                    _ = request.Work.Exception;
                    Status = request.Abandoned ? "Companion connector cancelled; waiting to retry." : "No validated companion connector found.";
                }
                request.Cancel.Dispose();
            }
        }
        if (active is { } connected)
        {
            if (!connected.MatchesTarget(mainRoute)) { active = null; return ordinary; }
            if (ReferenceEquals(motion.Route, mainRoute) && !motion.NeedsConnection)
            { active = null; Status = "Companion rejoined the current route."; return ordinary; }
            if (connected.OnMainSuffix(motion))
            {
                // Keep the plan until the next frame confirms adoption. If the
                // caller rejects a rebase, never fall through to global nearest
                // projection on a later frame (wrong at self-crossings).
                Status = "Companion is rejoining the current route.";
                // Preserve this exact occurrence on the main route, even when
                // another segment passes through the same world position.
                return ordinary with { RebaseArc = Math.Clamp(connected.JoinArc + motion.Arc - connected.ConnectorLength,
                    0, PathTrail.Length(mainRoute)) };
            }
            // A virtual target on the connector's main-route suffix guarantees
            // reaching the join even if the real player is behind that join.
            // Normal intent resumes as soon as the guide is actually on the suffix.
            return new(connected.Route, connected.ConnectorLength,
                new(CompanionIntentMode.Returning, 1, intent.GoalGlanceWeight, true), true,
                ReferenceEquals(motion.Route, connected.Route) ? null : 0);
        }
        if (pending is not null || !motion.HasPosition || !motion.NeedsConnection ||
            !GuideConnectorPlan.Point(motion.Position) || now - lastRequest < MinRequestInterval) return ordinary;
        if (!GuideConnectorPlan.TryJoin(mainRoute, motion.Position, out var joinArc, out var join)) return ordinary;
        lastRequest = now;
        var cancellation = new CancellationTokenSource();
        try
        {
            var work = query(motion.Position, join, cancellation.Token) ?? throw new InvalidOperationException("Missing connector query task.");
            pending = new(epoch, zone, mainRoute, (Vector3[])mainRoute.Clone(), motion.Position, join, joinArc, now, cancellation, work);
            Status = "Finding a walkable connection for the companion.";
        }
        catch (Exception)
        {
            cancellation.Dispose();
            Status = "Companion path query unavailable; holding its last confirmed position.";
        }
        return ordinary;
    }

    public void Reset()
    {
        Invalidate(); target = null; territory = 0; lastTime = double.NaN;
        ObserveCompleted();
    }

    private void Invalidate()
    {
        epoch++; active = null;
        if (pending is { } request) Cancel(request);
    }

    private void ObserveCompleted()
    {
        if (pending is not { Work.IsCompleted: true } request) return;
        _ = request.Work.Exception; request.Cancel.Dispose(); pending = null;
    }

    private static void Cancel(Pending request)
    {
        if (request.Abandoned) return;
        request.Abandoned = true;
        try { request.Cancel.Cancel(); } catch (AggregateException) { /* a provider callback cannot interrupt the game update */ }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Reset();
        if (pending is not { } request) return;
        pending = null;
        // Observe eventual faults even when the plugin has unloaded. This
        // continuation holds only managed query state, never plugin/game pointers.
        _ = request.Work.ContinueWith(task => { _ = task.Exception; request.Cancel.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
