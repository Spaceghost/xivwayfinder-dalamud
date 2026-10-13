using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>
/// Context changes when the destination, main route or guide appearance changes.
/// CanReposition is false while hidden, without a route/accepted guide, during an
/// owned jump, Teleport/boarding, or until an authored stand-up permits movement.
/// Radii are stable, conservative horizontal envelopes about the actual roots,
/// not a changing per-frame pose bound; replace them when the model/size changes.
/// Side follows MinionLayout: positive uses MinionGlove.Right(player heading).
/// </summary>
public readonly record struct CompanionIdleSnapshot(uint Territory, long Context, Vector3 Player,
    float PlayerYaw, float Side, float GuideRadius, float PlayerRadius, bool Waiting, bool CanReposition);

/// <summary>A stable query destination, never permission to place or move a model there.</summary>
public readonly record struct CompanionIdleRequest(long Generation, uint Territory, long Context,
    Vector3 PlayerAnchor, Vector3 Goal, float MinimumClearance);

/// <summary>
/// Chooses one clear side destination after the player settles. The caller owns
/// the navmesh query, route adoption and movement. Repeated turns cannot orbit
/// the companion; actual displacement or another movement owner cancels the request.
/// </summary>
public sealed class CompanionIdleRendezvous
{
    public const double StableSeconds = .9;
    public const double MaximumSampleGap = .5;
    public const float PlayerDriftTolerance = .025f;
    public const float VisualGap = .35f;
    public const float MaximumStandOff = 12;
    private CompanionIdleSnapshot episode;
    private bool observing;
    private double began, lastTime;
    private long generation;
    public CompanionIdleRequest? Current { get; private set; }

    public void Reset()
    {
        observing = false;
        Current = null;
        ++generation; // A late query can never match a later request at the same point.
    }

    public CompanionIdleRequest? Update(CompanionIdleSnapshot input, double now)
    {
        var clearance = input.GuideRadius + input.PlayerRadius + VisualGap;
        var distance = Math.Max(Math.Abs(input.Side), clearance);
        if (!input.Waiting || !input.CanReposition || input.Territory == 0
            || !GuideConnectorPlan.Point(input.Player) || !float.IsFinite(input.PlayerYaw)
            || !float.IsFinite(input.Side) || !float.IsFinite(input.GuideRadius) || input.GuideRadius <= 0
            || !float.IsFinite(input.PlayerRadius) || input.PlayerRadius <= 0
            || !float.IsFinite(distance) || distance > MaximumStandOff || !double.IsFinite(now))
        { Reset(); return null; }

        if (!observing || input.Territory != episode.Territory || input.Context != episode.Context
            || input.Side != episode.Side || input.GuideRadius != episode.GuideRadius
            || input.PlayerRadius != episode.PlayerRadius || now < lastTime || now - lastTime > MaximumSampleGap
            || Vector3.Distance(input.Player, episode.Player) > PlayerDriftTolerance)
        {
            Reset();
            episode = input; observing = true; began = lastTime = now;
            return null;
        }
        lastTime = now;
        if (Current is not null || now - began < StableSeconds) return Current;

        var side = MinionGlove.Right(Heading.Forward(episode.PlayerYaw)) * (input.Side < 0 ? -distance : distance);
        var goal = episode.Player + new Vector3(side.X, 0, side.Y);
        if (!GuideConnectorPlan.Point(goal)) { Reset(); return null; }
        Current = new(++generation, input.Territory, input.Context, episode.Player, goal, clearance);
        return Current;
    }

    /// <summary>
    /// Additional acceptance check on the endpoint of a complete queried route.
    /// Does not prove walkability, accept a partial route, append a missing leg,
    /// or establish arrival. Call Update with current lifecycle state first;
    /// the caller must separately validate the route and its accepted origin.
    /// </summary>
    public bool AcceptsQueriedEndpoint(CompanionIdleRequest request, Vector3 endpoint, Vector3 currentPlayer)
    {
        return Current is { } active && request == active
            && GuideConnectorPlan.Point(endpoint) && GuideConnectorPlan.Point(currentPlayer)
            && Vector3.Distance(currentPlayer, active.PlayerAnchor) <= PlayerDriftTolerance
            && Heading.FlatDistance(endpoint, active.Goal) <= CompanionApproachCoordinator.DestinationHorizontalTolerance
            && Math.Abs(endpoint.Y - active.Goal.Y) <= CompanionApproachCoordinator.DestinationVerticalTolerance
            && Heading.FlatDistance(endpoint, currentPlayer) >= active.MinimumClearance;
    }
}
