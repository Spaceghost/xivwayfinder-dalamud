using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>Minimal upward correction against one confirmed collision triangle.
/// This proves clearance only over that triangle, never adjacent terrain or props.</summary>
public static class GuideModelSupport
{
    // A distance tolerance, not barycentric area: tiny triangles and long edges
    // must not authorize a visibly different footprint at a shared seam.
    private const double EdgeTolerance = .00001;

    /// <summary>Preserves the renderer's existing height, including authored bounce.
    /// Returns false without a correction if any rendered vertex lies outside the
    /// confirmed triangle, or if the hit does not agree with its geometry.
    /// Convex triangle containment of every rendered vertex also covers each mesh face.</summary>
    public static bool TryGetLift(ReadOnlySpan<Vector3> worldVertices, ReadOnlySpan<int> indices,
        TrailSurfaceHit support, float clearance, out float lift)
    {
        lift = 0;
        if (worldVertices.IsEmpty || indices.IsEmpty || indices.Length % 3 != 0
            || !float.IsFinite(clearance) || clearance < 0
            || !TrailPlacement.IsFinite(support.Point) || !TrailPlacement.IsFinite(support.A)
            || !TrailPlacement.IsFinite(support.B) || !TrailPlacement.IsFinite(support.C)) return false;
        // Use double differences in a local frame: world offsets must not eat
        // the precision needed to distinguish an edge from an uncovered point.
        var ax = (double)support.A.X; var ay = (double)support.A.Y; var az = (double)support.A.Z;
        var bx = support.B.X - ax; var by = support.B.Y - ay; var bz = support.B.Z - az;
        var cx = support.C.X - ax; var cy = support.C.Y - ay; var cz = support.C.Z - az;
        var area = bx * cz - bz * cx;
        var nx = by * cz - bz * cy; var nz = bx * cy - by * cx;
        var normalLength = Math.Sqrt(nx * nx + area * area + nz * nz);
        if (!double.IsFinite(normalLength) || normalLength == 0 || Math.Abs(area) / normalLength < .35) return false;
        var slopeX = nx / area; var slopeZ = nz / area;
        var sign = Math.Sign(area);
        var abTolerance = EdgeTolerance * Math.Sqrt(bx * bx + bz * bz);
        var bcTolerance = EdgeTolerance * Math.Sqrt((cx - bx) * (cx - bx) + (cz - bz) * (cz - bz));
        var caTolerance = EdgeTolerance * Math.Sqrt(cx * cx + cz * cz);
        if (!Height(support.Point, out var hitHeight) || Math.Abs(hitHeight - support.Point.Y) > .01) return false;
        var required = 0d;
        foreach (var index in indices)
        {
            if ((uint)index >= (uint)worldVertices.Length) return false;
            var point = worldVertices[index];
            if (!TrailPlacement.IsFinite(point) || !Height(point, out var floor)) return false;
            required = Math.Max(required, floor + clearance - point.Y);
        }
        if (!double.IsFinite(required) || required > float.MaxValue) return false;
        var result = (float)required;
        // The float conversion may round downward. Never understate the
        // computed clearance, while leaving already-clear geometry unchanged.
        if (result < required) result = MathF.BitIncrement(result);
        lift = result;
        return true;

        bool Height(Vector3 point, out double height)
        {
            height = 0;
            var px = point.X - ax; var pz = point.Z - az;
            if (sign * (bx * pz - bz * px) < -abTolerance
                || sign * ((cx - bx) * (pz - bz) - (cz - bz) * (px - bx)) < -bcTolerance
                || sign * (-cx * (pz - cz) + cz * (px - cx)) < -caTolerance) return false;
            height = ay + px * slopeX + pz * slopeZ;
            return double.IsFinite(height);
        }
    }
}
