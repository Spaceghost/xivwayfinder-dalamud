using System.Numerics;
using System.Diagnostics;

namespace XivWayfinder.Core;

/// <summary>Only the collision geometry needed to position a trail mark.</summary>
public readonly record struct TrailSurfaceHit(Vector3 Point, Vector3 Normal, Vector3 A, Vector3 B, Vector3 C);

/// <summary>
/// Resolves route samples onto collision surfaces without ever falling back to an unverified route height.
/// Samples keep their exact coordinates: even nearby points can be on opposite sides of a stair or small prop.
/// Separate bounded budgets let the dots and connecting line both make progress without a large raycast burst.
/// </summary>
public sealed class TrailSurfaceSampler(Func<Vector3, Vector3, float, TrailSurfaceHit?> raycast, Func<double>? clock = null)
{
    private const int FloorQueriesPerFrame = 4;
    private const int SegmentQueriesPerFrame = 4;
    private const int AdaptiveSegmentLimit = 128;
    private const double SegmentTimeBudget = 0.0005;
    private const double SuccessLifetime = 2;
    private const double RefreshAfter = 1.25;
    private const double FailureLifetime = 0.25;
    private const int CacheLimit = 8192;
    private readonly Dictionary<Vector3, (double At, Vector3? Floor, Vector3 Normal)> floors = [];
    private readonly Dictionary<(Vector3 A, Vector3 B), (double At, bool Clear)> segments = [];
    private int floorQueries;
    private int segmentQueries;
    private int segmentQueriesUsed;
    private double segmentSeconds;
    private bool adaptiveSegments;
    private readonly Func<double> queryClock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private uint territory;

    public void Begin(uint zone, bool allowAdaptiveSegments = false)
    {
        floorQueries = FloorQueriesPerFrame;
        adaptiveSegments = allowAdaptiveSegments;
        segmentQueries = adaptiveSegments ? AdaptiveSegmentLimit : SegmentQueriesPerFrame;
        segmentQueriesUsed = 0;
        segmentSeconds = 0;
        if (territory != zone)
        {
            floors.Clear();
            segments.Clear();
            territory = zone;
        }
        // Moving overhead heights create new segment keys each frame. Their eviction must not throw away
        // stable route-floor samples, otherwise the dots and line repeatedly go cold during a descent.
        if (floors.Count > CacheLimit) floors.Clear();
        if (segments.Count > CacheLimit) segments.Clear();
    }

    public bool TryPlace(Vector3 point, double now, out Vector3 at)
        => TryPlace(point, now, out at, out _);

    public bool TryPlace(Vector3 point, double now, out Vector3 at, out Vector3 normal)
    {
        at = default;
        normal = default;
        if (!TrailPlacement.IsFinite(point) || !double.IsFinite(now)) return false;
        var present = floors.TryGetValue(point, out var entry);
        var fresh = present && Fresh(entry.At, now, entry.Floor.HasValue ? SuccessLifetime : FailureLifetime);
        // Refresh still-valid samples before their hard deadline, reserving half the budget for new/expired
        // samples. Otherwise an initially warmed route loses many segments together at the two-second mark,
        // or refreshing its near end starves the newly visible far end while the player is moving.
        if (!fresh || (entry.Floor.HasValue && now - entry.At >= RefreshAfter && floorQueries > FloorQueriesPerFrame / 2))
        {
            if (floorQueries <= 0) return false;
            floorQueries--;
            // Start close to the route: a ray from 1.55 yalms overhead can hit
            // an unrelated upper deck before the floor the route actually uses.
            const float nearAbove = .25f;
            var hit = raycast(point + Vector3.UnitY * nearAbove, -Vector3.UnitY,
                TrailPlacement.FloorHeightSlack + nearAbove);
            Vector3? floor = hit is { } near && near.Point.Y <= point.Y + nearAbove + .001f
                && TrailPlacement.TryGround(point, near, out var placed) ? placed : null;
            if (!floor.HasValue)
            {
                // Approximate navmesh heights can be below a tread. The wider
                // recovery ray retains that support, but counts as a second ray.
                if (floorQueries <= 0) return false;
                floorQueries--;
                hit = raycast(point + Vector3.UnitY * (TrailPlacement.FloorHeightSlack + 0.05f),
                    -Vector3.UnitY, TrailPlacement.FloorHeightSlack * 2 + 0.1f);
                floor = hit is { } found && TrailPlacement.TryGround(point, found, out placed) ? placed : null;
            }
            var surfaceNormal = floor.HasValue && hit is { } surface
                ? TrailPlacement.SurfaceNormal(surface.Normal, surface.A, surface.B, surface.C) : Vector3.Zero;
            entry = (now, floor, surfaceNormal);
            floors[point] = entry;
        }
        if (entry.Floor is not { } verified) return false;
        at = verified;
        normal = entry.Normal;
        return true;
    }

    public bool TryPlaceOverhead(Vector3 point, Vector3 player, float height, double now, out Vector3 at)
    {
        at = default;
        if (!TrailPlacement.IsFinite(player) || !TryPlace(point, now, out var ground)) return false;
        at = TrailPlacement.Overhead(ground, player, height);
        return TrailPlacement.IsFinite(at);
    }

    /// <summary>
    /// Verified endpoints alone do not prove their joining line is clear: a stair edge, ridge or prop can rise
    /// between them. Missing budget leaves a gap until the segment is checked, rather than drawing through it.
    /// </summary>
    public bool CanConnect(Vector3 a, Vector3 b, double now)
    {
        if (!TrailPlacement.IsFinite(a) || !TrailPlacement.IsFinite(b) || !double.IsFinite(now)) return false;
        var delta = b - a;
        var distance = delta.Length();
        if (!float.IsFinite(distance) || distance < 0.001f || distance > 2f) return false;
        var key = (a, b);
        var present = segments.TryGetValue(key, out var entry);
        var fresh = present && Fresh(entry.At, now, entry.Clear ? SuccessLifetime : FailureLifetime);
        // An overhead segment changes height as a player descends, even when its route coordinates are fixed.
        // Allow those exact new segments to be checked in the same frame when rays are cheap, without ever
        // reusing a nearby-height answer. Slow collision queries retain the previous four-query bound.
        var canQuery = segmentQueries > 0 && (!adaptiveSegments || segmentQueriesUsed < SegmentQueriesPerFrame || segmentSeconds < SegmentTimeBudget);
        if (!fresh || (entry.Clear && now - entry.At >= RefreshAfter && segmentQueries > SegmentQueriesPerFrame / 2 && canQuery))
        {
            if (!canQuery) return false;
            segmentQueries--;
            segmentQueriesUsed++;
            // Test the actual lifted segment; a hit of any material breaks it, including a near-end hit.
            var started = adaptiveSegments ? queryClock() : 0;
            var clear = raycast(a, delta / distance, distance) is null;
            if (adaptiveSegments) segmentSeconds += Math.Max(0, queryClock() - started);
            entry = (now, clear);
            segments[key] = entry;
        }
        return entry.Clear;
    }

    private static bool Fresh(double at, double now, double lifetime) => now >= at && now - at < lifetime;
}
