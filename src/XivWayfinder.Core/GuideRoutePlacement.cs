using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>
/// Conservative synchronous foot-root preflight for imported route models.
/// Navmesh owns the path; these three rays veto missing floors and intervening
/// walls. This is not a full-body sweep or proof of unknown traversal geometry.
/// </summary>
public static class GuideRoutePlacement
{
    public const float MaximumStep = 8;
    public static bool SameHeldPose(MinionPose candidate, MinionPose accepted) => Valid(candidate) && Valid(accepted)
        && Vector3.DistanceSquared(candidate.Position, accepted.Position) <= .000001f
        && Math.Abs(MathF.IEEERemainder(candidate.Yaw - accepted.Yaw, MathF.Tau)) <= .001f
        && Math.Abs(candidate.Pitch - accepted.Pitch) <= .001f;

    public static bool TryPlace(MinionPose candidate, float scale, Vector3? previousFloor,
        Func<Vector3, Vector3, float, TrailSurfaceHit?> raycast, out Vector3 floor)
    {
        floor = default;
        if (!Valid(candidate) || !float.IsFinite(scale) || scale is <= 0 or > 4
            || previousFloor is { } old && !TrailPlacement.IsFinite(old)) return false;
        var point = candidate.Position;
        if (previousFloor is { } previous && Vector2.Distance(new(previous.X, previous.Z), new(point.X, point.Z)) > MaximumStep) return false;
        var hit = raycast(point + Vector3.UnitY * 1.5f, -Vector3.UnitY, 3);
        if (hit is not { } support || !TrailPlacement.IsFinite(support.Point)
            || TrailPlacement.SurfaceNormal(support.Normal, support.A, support.B, support.C).Y < .35f
            || Math.Abs(support.Point.Y - point.Y) > 1.25f
            || Math.Abs(support.Point.X - point.X) > .02f || Math.Abs(support.Point.Z - point.Z) > .02f) return false;
        floor = new(point.X, support.Point.Y, point.Z);
        if (previousFloor is not { } from) return true;
        var delta = floor - from;
        var distance = delta.Length();
        if (!float.IsFinite(distance) || distance > MaximumStep) return false;
        if (distance < .01f) return true;
        // Follow the accepted segment's slope at two bounded body heights,
        // rather than casting a horizontal chord through an ordinary stair.
        for (var i = 0; i < 2; i++)
        {
            var wall = raycast(from + Vector3.UnitY * (i == 0 ? .3f : .8f), delta / distance, distance);
            if (wall is { } obstruction && (!TrailPlacement.IsFinite(obstruction.Point)
                || TrailPlacement.SurfaceNormal(obstruction.Normal, obstruction.A, obstruction.B, obstruction.C).Y < .55f)) return false;
        }
        return true;
    }

    private static bool Valid(MinionPose pose) => TrailPlacement.IsFinite(pose.Position)
        && float.IsFinite(pose.Yaw) && float.IsFinite(pose.Pitch);
}
