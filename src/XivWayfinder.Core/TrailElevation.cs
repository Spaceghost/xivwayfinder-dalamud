using System.Numerics;
namespace XivWayfinder.Core;

public enum TrailElevation { Ground, Overhead }
public static class TrailPlacement
{
    public const float GroundClearance = 0.16f;
    public const float FloorHeightSlack = 1.5f;

    public static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static Vector3 SurfaceNormal(Vector3 supplied, Vector3 a, Vector3 b, Vector3 c)
    {
        var n = IsFinite(supplied) && supplied.LengthSquared() > 1e-10f ? supplied : Vector3.Cross(b - a, c - a);
        if (!float.IsFinite(n.LengthSquared()) || n.LengthSquared() < 1e-10f) return Vector3.Zero;
        n = Vector3.Normalize(n);
        return n.Y < 0 ? -n : n;
    }

    /// <summary>A collision hit must belong to the downward ray and the route's floor, not a nearby wall or another storey.</summary>
    public static bool TryGround(Vector3 route, TrailSurfaceHit hit, out Vector3 at)
    {
        at = default;
        if (!IsFinite(route) || !IsFinite(hit.Point) ||
            SurfaceNormal(hit.Normal, hit.A, hit.B, hit.C).Y <= 0.35f ||
            MathF.Abs(hit.Point.Y - route.Y) > FloorHeightSlack ||
            MathF.Abs(hit.Point.X - route.X) > 0.02f || MathF.Abs(hit.Point.Z - route.Z) > 0.02f)
            return false;

        at = new Vector3(route.X, hit.Point.Y + GroundClearance, route.Z);
        return IsFinite(at);
    }

    // Preserve each verified route floor, including descents below the player's current floor.
    public static Vector3 Overhead(Vector3 route, Vector3 player, float height) => route with { Y = route.Y + Math.Clamp(float.IsFinite(height) ? height : 3f, 2.5f, 12f) };
}
