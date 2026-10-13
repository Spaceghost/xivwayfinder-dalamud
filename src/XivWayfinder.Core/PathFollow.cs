using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>The next leg of a real turn and how strongly the glove should lean into it.</summary>
public readonly record struct CornerWarning(Vector2 Direction, float Blend);

/// <summary>
/// Following a walkable path (vnavmesh's corners) without walking it: which corner to point at from where the
/// player stands now, and when the path has gone stale and should be asked for again. Only the ground plane
/// counts, as for straight-line pointing.
/// </summary>
public static class PathFollow
{
    /// <summary>
    /// A warning when <paramref name="player"/> is close to a genuine interior corner. Short or malformed paths,
    /// the final point, gentle bends below <paramref name="minTurnDegrees"/>, and points further than
    /// <paramref name="distance"/> return null. Height is deliberately ignored.
    /// </summary>
    public static CornerWarning? ApproachingCorner(
        ReadOnlySpan<Vector3> corners,
        int corner,
        Vector3 player,
        float distance = 3f,
        float minTurnDegrees = 30f)
    {
        if (corner <= 0 || corner + 1 >= corners.Length || !(distance > 0f))
            return null;
        var at = Flat(corners[corner]);
        var away = Vector2.Distance(Flat(player), at);
        if (away > distance)
            return null;
        var incoming = at - Flat(corners[corner - 1]);
        var outgoing = Flat(corners[corner + 1]) - at;
        if (incoming.LengthSquared() < 1e-6f || outgoing.LengthSquared() < 1e-6f)
            return null;
        incoming = Vector2.Normalize(incoming);
        outgoing = Vector2.Normalize(outgoing);
        var turn = MathF.Acos(Math.Clamp(Vector2.Dot(incoming, outgoing), -1f, 1f)) * 180f / MathF.PI;
        if (turn < Math.Clamp(minTurnDegrees, 0f, 180f))
            return null;
        return new CornerWarning(outgoing, 1f - Pulse.SmoothStep(0f, distance, away));
    }

    /// <summary>
    /// The index of the corner to point at. The player is placed on the nearest segment of the path (so cutting a
    /// corner or wandering back never points backwards), and the pointer aims at that segment's end, or the one
    /// after when the end is already within <paramref name="reach"/>. -1 for an empty path.
    /// </summary>
    public static int NextCorner(ReadOnlySpan<Vector3> corners, Vector3 player, float reach)
    {
        if (corners.Length == 0)
            return -1;
        if (corners.Length == 1)
            return 0;
        var p = new Vector2(player.X, player.Z);
        var bestSegment = 0;
        var bestDistance = float.PositiveInfinity;
        for (var i = 0; i + 1 < corners.Length; i++)
        {
            var d = DistanceToSegment(p, Flat(corners[i]), Flat(corners[i + 1]), out _);
            // ties go to the later segment: at a shared corner the player is already past the earlier one
            if (d <= bestDistance)
            {
                bestDistance = d;
                bestSegment = i;
            }
        }

        var next = bestSegment + 1;
        while (next < corners.Length - 1 && Vector2.Distance(p, Flat(corners[next])) <= reach)
            next++;
        return next;
    }

    /// <summary>How far the player is from the path, on the ground; infinity for an empty path.</summary>
    public static float DistanceFromPath(ReadOnlySpan<Vector3> corners, Vector3 player)
    {
        if (corners.Length == 0)
            return float.PositiveInfinity;
        var p = new Vector2(player.X, player.Z);
        if (corners.Length == 1)
            return Vector2.Distance(p, Flat(corners[0]));
        var best = float.PositiveInfinity;
        for (var i = 0; i + 1 < corners.Length; i++)
            best = MathF.Min(best, DistanceToSegment(p, Flat(corners[i]), Flat(corners[i + 1]), out _));
        return best;
    }

    /// <summary>
    /// Whether to ask for a new path: never while one is being worked out or sooner than
    /// <paramref name="minInterval"/> seconds after the last request; otherwise when there is none yet, the target
    /// moved more than a yalm, or the player strayed further than <paramref name="offPath"/> from the path.
    /// </summary>
    public static bool ShouldRequery(bool hasPath, bool inFlight, double sinceLastRequest, double minInterval, Vector3 pathGoal, Vector3 goal, float distanceFromPath, float offPath)
    {
        if (inFlight || sinceLastRequest < minInterval)
            return false;
        if (!hasPath)
            return true;
        if (Heading.FlatDistance(pathGoal, goal) > 1f)
            return true;
        return distanceFromPath > offPath;
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared();
        t = len2 < 1e-8f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }
}
