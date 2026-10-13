using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>A continuous position on an existing walkable route, never a new route or movement command.</summary>
public readonly record struct PathProgress(float Arc, float Away, int NextCorner, bool Relocated)
{
    public bool Valid => NextCorner >= 0;
    public static PathProgress Empty => new(0, float.PositiveInfinity, -1, false);
}

/// <summary>
/// Tracks relative progress, not the nearest waypoint. A bounded arc neighbourhood
/// preserves the current branch at crossings/parallel legs; movement, not elapsed
/// frames, bounds progress, so standing still cannot creep onto a different leg.
/// Small backward projection jitter is ignored, but deliberate backtracking works.
/// Reset whenever a route is replaced. Only samples the supplied navmesh segments.
/// </summary>
public sealed class PathProgressTracker
{
    private const float BackwardSlack = .15f;
    private const float BranchSlack = 1f;
    private const float RelocationDistance = 8f;
    private Vector3? previousPlayer;
    public PathProgress Current { get; private set; } = PathProgress.Empty;

    public void Reset() { previousPlayer = null; Current = PathProgress.Empty; }

    public PathProgress Update(ReadOnlySpan<Vector3> path, Vector3 player)
    {
        if (path.Length < 2 || !Finite(player)) { Reset(); return Current; }
        foreach (var point in path) if (!Finite(point)) { Reset(); return Current; }
        var total = PathTrail.Length(path);
        if (!float.IsFinite(total) || total < 1e-4f) { Reset(); return Current; }
        var initialized = previousPlayer is not null && Current.Valid;
        var moved = initialized ? Heading.FlatDistance(previousPlayer!.Value, player) : 0;
        var relocated = Current.Relocated || initialized &&
            (moved > RelocationDistance || Math.Abs(previousPlayer!.Value.Y - player.Y) > RelocationDistance);
        // A discontinuity stays invalid for following until a new navmesh request
        // replaces the route; do not select a distant branch after a teleport.
        var movementBudget = relocated ? 0 : moved * 2;
        var lower = initialized ? Math.Max(0, Current.Arc - movementBudget - BranchSlack) : 0;
        var upper = initialized ? Math.Min(total, Current.Arc + movementBudget + BranchSlack) : total;
        var arc = 0f; var bestArc = Current.Arc; var bestScore = float.PositiveInfinity;
        var bestContinuity = float.PositiveInfinity;
        for (var i = 0; i + 1 < path.Length; i++)
        {
            var a = path[i]; var b = path[i + 1];
            var delta = new Vector2(b.X - a.X, b.Z - a.Z); var length = delta.Length();
            if (length < 1e-4f) continue;
            var end = arc + length;
            if (end >= lower && arc <= upper)
            {
                var t = Math.Clamp(Vector2.Dot(new(player.X - a.X, player.Z - a.Z), delta) / (length * length),
                    Math.Clamp((lower - arc) / length, 0, 1), Math.Clamp((upper - arc) / length, 0, 1));
                var at = Vector3.Lerp(a, b, t); var candidate = arc + length * t;
                var score = Distance(player, at);
                var continuity = initialized ? Math.Abs(candidate - Current.Arc) : candidate;
                if (score < bestScore - .001f || Math.Abs(score - bestScore) <= .001f && continuity < bestContinuity)
                { bestScore = score; bestArc = candidate; bestContinuity = continuity; }
            }
            arc = end;
        }
        if (!float.IsFinite(bestScore)) { Reset(); return Current; }
        if (initialized)
        {
            if (bestArc < Current.Arc && Current.Arc - bestArc <= BackwardSlack) bestArc = Current.Arc;
            bestArc = Math.Clamp(bestArc, Current.Arc - movementBudget, Current.Arc + movementBudget);
        }
        bestArc = Math.Clamp(bestArc, 0, total);
        var next = path.Length - 1; arc = 0;
        for (var i = 0; i + 1 < path.Length; i++)
        {
            arc += Heading.FlatDistance(path[i], path[i + 1]);
            if (arc > bestArc + .001f) { next = i + 1; break; }
        }
        previousPlayer = player;
        Current = new(bestArc, Distance(player, PathTrail.At(path, bestArc)), next, relocated);
        return Current;
    }

    private static float Distance(Vector3 player, Vector3 at) => Heading.FlatDistance(player, at)
        + Math.Max(0, Math.Abs(at.Y - player.Y) - PathTrail.HeightSlack);
    private static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
