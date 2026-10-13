using System.Numerics;

namespace XivWayfinder.Core;

public readonly record struct GuideJumpFloorTriangle(Vector3 A, Vector3 B, Vector3 C);

/// <summary>Exact segment/triangle interval coverage, not interpolation between isolated floor samples.</summary>
public static class GuideJumpFloorCoverage
{
    public static bool Covers(GuideJumpPlan plan, ReadOnlySpan<GuideJumpFloorTriangle> triangles)
    {
        if (plan is not { Valid: true } || triangles.Length is < 1 or > GuideJumpRequest.FloorSampleCount) return false;
        Span<Interval> intervals = stackalloc Interval[GuideJumpRequest.FloorSampleCount];
        var count = 0;
        foreach (var triangle in triangles)
        {
            if (!TryInterval(plan, triangle, out var interval, out var obstructsFeet))
            { if (obstructsFeet) return false; continue; }
            var at = count++;
            while (at > 0 && intervals[at - 1].Start > interval.Start)
            { intervals[at] = intervals[at - 1]; at--; }
            intervals[at] = interval;
        }
        // Float-roundoff only (at most 0.000022 yalms for this bounded hop).
        // This is not a configurable gap/jump tolerance.
        const double roundoff = .00001;
        var covered = 0d;
        for (var i = 0; i < count; ++i)
        {
            if (intervals[i].Start > covered + roundoff) return false;
            covered = Math.Max(covered, intervals[i].End);
            if (covered >= 1 - roundoff) return true;
        }
        return false;
    }

    private readonly record struct Interval(double Start, double End);

    private static bool TryInterval(GuideJumpPlan plan, GuideJumpFloorTriangle triangle, out Interval interval, out bool obstructsFeet)
    {
        interval = default; obstructsFeet = false;
        var (a, b, c) = triangle;
        if (!TrailPlacement.IsFinite(a) || !TrailPlacement.IsFinite(b) || !TrailPlacement.IsFinite(c)) return false;
        var normal = Vector3.Cross(b - a, c - a);
        if (!TrailPlacement.IsFinite(normal) || normal.LengthSquared() < 1e-12f
            || Math.Abs(normal.Y) / normal.Length() < .55f) return false;
        var denominator = (double)(b.Z - c.Z) * (a.X - c.X) + (double)(c.X - b.X) * (a.Z - c.Z);
        if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-12) return false;
        var start = Coordinates(plan.Request.Start); var end = Coordinates(plan.Request.End);
        var low = 0d; var high = 1d;
        if (!Clip(start.U, end.U, ref low, ref high) || !Clip(start.V, end.V, ref low, ref high)
            || !Clip(1 - start.U - start.V, 1 - end.U - end.V, ref low, ref high)) return false;
        // A huge triangle on another floor, or a steep extension beyond its
        // sampled point, cannot fill an unknown hole in the supported surface.
        var firstHeight = start.U * a.Y + start.V * b.Y + (1 - start.U - start.V) * c.Y - plan.Takeoff.Y;
        var lastHeight = end.U * a.Y + end.V * b.Y + (1 - end.U - end.V) * c.Y - plan.Landing.Y;
        // The foot parabola minus this plane is concave, so its minimum over
        // this triangle's interval is at one of the endpoints. This also
        // catches raised edges between the original downward ray samples.
        double Clearance(double at) => GuideJumpPolicy.FootClearance + 4 * plan.Apex * at * (1 - at)
            - (firstHeight + (lastHeight - firstHeight) * at);
        if (Clearance(low) < .015 || Clearance(high) < .015)
        { obstructsFeet = true; return false; }
        if (!Clip(firstHeight + .06, lastHeight + .06, ref low, ref high)
            || !Clip(GuideJumpPolicy.MaximumObstacle - firstHeight, GuideJumpPolicy.MaximumObstacle - lastHeight, ref low, ref high)) return false;
        interval = new(low, high);
        return high >= low;

        (double U, double V) Coordinates(Vector3 p) => (
            ((double)(b.Z - c.Z) * (p.X - c.X) + (double)(c.X - b.X) * (p.Z - c.Z)) / denominator,
            ((double)(c.Z - a.Z) * (p.X - c.X) + (double)(a.X - c.X) * (p.Z - c.Z)) / denominator);
    }

    private static bool Clip(double first, double last, ref double low, ref double high)
    {
        if (!double.IsFinite(first) || !double.IsFinite(last)) return false;
        var change = last - first;
        if (Math.Abs(change) < 1e-12) return first >= -1e-12;
        var crossing = -first / change;
        if (change > 0) low = Math.Max(low, crossing);
        else high = Math.Min(high, crossing);
        return high >= low;
    }
}
