using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>A point of the trail: where it is in the world, and how far along the route ahead of the player.</summary>
public readonly record struct TrailPoint(Vector3 Position, float Ahead);

/// <summary>
/// Path highlighting: the walkable route (vnavmesh's corners) measured by distance along it, so the trail's beads
/// sit at fixed places on the route (every <c>spacing</c> yalms from its start) instead of on a straight line from
/// the player. The player is placed on the route, the next stretch ahead is sampled, and beads the player has
/// walked past are simply no longer ahead. Distances along the route are on the ground plane, as everywhere else;
/// heights are interpolated between corners.
/// </summary>
public static class PathTrail
{
    /// <summary>A corner more than this far above or below the player counts against its segment, so a ramp overhead is not mistaken for the one underfoot.</summary>
    public const float HeightSlack = 2f;

    /// <summary>The route's length on the ground, yalms.</summary>
    public static float Length(ReadOnlySpan<Vector3> corners)
    {
        var total = 0f;
        for (var i = 0; i + 1 < corners.Length; i++)
            total += Flat(corners[i], corners[i + 1]);
        return total;
    }

    /// <summary>
    /// Where the player is along the route: the distance from its start to the nearest point on it. Nearest is on
    /// the ground, with a penalty for segments much higher or lower than the player's feet, and ties go to the
    /// later segment (at a shared corner the player is already past the earlier one), as <see cref="PathFollow"/>
    /// does. <paramref name="away"/> is the ground distance from the route. 0 and infinity for an empty route.
    /// </summary>
    public static float Project(ReadOnlySpan<Vector3> corners, Vector3 player, out float away)
    {
        away = float.PositiveInfinity;
        if (corners.Length == 0)
            return 0f;
        var p = new Vector2(player.X, player.Z);
        if (corners.Length == 1)
        {
            away = Vector2.Distance(p, new Vector2(corners[0].X, corners[0].Z));
            return 0f;
        }

        var best = float.PositiveInfinity;
        var bestArc = 0f;
        var arc = 0f;
        for (var i = 0; i + 1 < corners.Length; i++)
        {
            var a = corners[i];
            var b = corners[i + 1];
            var fa = new Vector2(a.X, a.Z);
            var fb = new Vector2(b.X, b.Z);
            var ab = fb - fa;
            var len = ab.Length();
            var t = len < 1e-4f ? 0f : Math.Clamp(Vector2.Dot(p - fa, ab) / (len * len), 0f, 1f);
            var ground = Vector2.Distance(p, fa + ab * t);
            var height = MathF.Abs(a.Y + (b.Y - a.Y) * t - player.Y);
            var score = ground + MathF.Max(0f, height - HeightSlack);
            if (score <= best)
            {
                best = score;
                bestArc = arc + len * t;
                away = ground;
            }

            arc += len;
        }

        return bestArc;
    }

    /// <summary>The point <paramref name="arc"/> yalms along the route from its start, clamped to its ends.</summary>
    public static Vector3 At(ReadOnlySpan<Vector3> corners, float arc)
    {
        if (corners.Length == 0)
            return Vector3.Zero;
        if (!(arc > 0f))
            return corners[0];
        for (var i = 0; i + 1 < corners.Length; i++)
        {
            var len = Flat(corners[i], corners[i + 1]);
            if (arc <= len)
                return len < 1e-4f ? corners[i + 1] : Vector3.Lerp(corners[i], corners[i + 1], arc / len);
            arc -= len;
        }

        return corners[^1];
    }

    /// <summary>
    /// The trail ahead of a player <paramref name="playerArc"/> yalms along the route: points at every multiple of
    /// <paramref name="spacing"/> from the route's start that lies more than <paramref name="start"/> and at most
    /// <paramref name="length"/> yalms ahead, never past the route's end. Fixed to the route, so they stay put in
    /// the world as the player walks, and drop away behind. Returns how many were written.
    /// </summary>
    public static int Sample(ReadOnlySpan<Vector3> corners, float playerArc, float spacing, float start, float length, Span<TrailPoint> into)
    {
        if (corners.Length < 2 || into.Length == 0 || !(spacing > 0.05f) || !float.IsFinite(playerArc) || !(length > start))
            return 0;
        var total = Length(corners);
        var from = playerArc + MathF.Max(0f, start);
        var to = MathF.Min(playerArc + length, total);
        var k = MathF.Floor(from / spacing) + 1f;
        var n = 0;

        // walk the segments once, in step with the samples
        var segment = 0;
        var segmentStart = 0f;
        var segmentLength = Flat(corners[0], corners[1]);
        while (n < into.Length)
        {
            var arc = k * spacing;
            if (arc > to || !float.IsFinite(arc))
                break;
            while (arc > segmentStart + segmentLength && segment + 2 < corners.Length)
            {
                segmentStart += segmentLength;
                segment++;
                segmentLength = Flat(corners[segment], corners[segment + 1]);
            }

            var t = segmentLength < 1e-4f ? 1f : Math.Clamp((arc - segmentStart) / segmentLength, 0f, 1f);
            into[n++] = new TrailPoint(Vector3.Lerp(corners[segment], corners[segment + 1], t), arc - playerArc);
            k += 1f;
        }

        return n;
    }

    /// <summary>
    /// A point <paramref name="distance"/> yalms further along the route than the player (the route's end when it
    /// is nearer): what to point at so the pointer follows the route's bends instead of the target.
    /// </summary>
    public static Vector3 LookAhead(ReadOnlySpan<Vector3> corners, float playerArc, float distance) =>
        At(corners, playerArc + MathF.Max(0f, distance));

    /// <summary>The straight-line fallback: <paramref name="count"/> points every <paramref name="spacing"/> yalms from 2 yalms out, towards the target.</summary>
    public static int Straight(Vector3 player, Vector2 dir, float spacing, int count, float total, Span<TrailPoint> into)
    {
        if (dir == Vector2.Zero)
            return 0;
        var n = 0;
        for (var i = 0; i < count && n < into.Length; i++)
        {
            var along = 2f + i * spacing;
            if (along >= total)
                break;
            into[n++] = new TrailPoint(Heading.Ahead(player, dir, along, 0f), along);
        }

        return n;
    }

    private static float Flat(Vector3 a, Vector3 b) => Heading.FlatDistance(a, b);
}
