using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>A root-relative cylinder enclosing scaled native resource bounds.
/// This is not a sampled skin, a collision hull, or a whole-animation envelope.</summary>
public readonly record struct GuideNativeEnvelope(float Radius, float Height, float MinimumY);

/// <summary>
/// CharacterBase's native AABB includes world translation but omits draw scale
/// and its two character scale factors. Scale offsets, never world positions.
/// Callers must separately validate pose coverage and their floor/root policy.
/// </summary>
public static class GuideNativeBounds
{
    public const float RootTolerance = .02f;

    /// <summary>Do not authorize a yaw-only envelope from a stale draw root,
    /// tilted model, or malformed quaternion. A later grounded update can
    /// establish the upright pose; this preflight never writes the actor.</summary>
    public static bool MatchesUprightRoot(Vector3 drawPosition, Vector3 acceptedFootPosition, Quaternion rotation) =>
        Finite(drawPosition) && Finite(acceptedFootPosition)
        && Vector3.DistanceSquared(drawPosition, acceptedFootPosition) <= RootTolerance * RootTolerance
        && float.IsFinite(rotation.X) && float.IsFinite(rotation.Y) && float.IsFinite(rotation.Z) && float.IsFinite(rotation.W)
        && rotation.X == 0 && rotation.Z == 0 && Math.Abs(rotation.LengthSquared() - 1) <= .0001f;

    public static bool TryMeasureSize(Vector3 min, Vector3 max, Vector3 drawScale,
        float firstScaleFactor, float secondScaleFactor, out Vector3 worldSize)
    {
        worldSize = default;
        if (!ValidBounds(min, max) || !TryScale(drawScale, firstScaleFactor, secondScaleFactor, out var scale)) return false;
        var size = new Vector3(
            (float)(((double)max.X - min.X) * scale.X),
            (float)(((double)max.Y - min.Y) * scale.Y),
            (float)(((double)max.Z - min.Z) * scale.Z));
        if (!Finite(size) || Math.Max(size.X, Math.Max(size.Y, size.Z)) <= .0001f) return false;
        worldSize = size;
        return true;
    }

    /// <summary>
    /// BoundsOrigin is the draw position added by the native AABB routine,
    /// not a desired route position. XZ covers every yaw; pitch/roll and future
    /// animation deformation need separate proof. MinimumY remains signed so
    /// callers cannot silently discard geometry below the approved foot root.
    /// </summary>
    public static bool TryMeasureRootEnvelope(Vector3 min, Vector3 max, Vector3 boundsOrigin,
        Vector3 drawScale, float firstScaleFactor, float secondScaleFactor, out GuideNativeEnvelope envelope)
    {
        envelope = default;
        if (!ValidBounds(min, max) || !Finite(boundsOrigin)
            || !TryScale(drawScale, firstScaleFactor, secondScaleFactor, out var scale)) return false;
        var x = Math.Max(Math.Abs((double)min.X - boundsOrigin.X), Math.Abs((double)max.X - boundsOrigin.X)) * scale.X;
        var z = Math.Max(Math.Abs((double)min.Z - boundsOrigin.Z), Math.Abs((double)max.Z - boundsOrigin.Z)) * scale.Z;
        var low = ((double)min.Y - boundsOrigin.Y) * scale.Y;
        var high = ((double)max.Y - boundsOrigin.Y) * scale.Y;
        // Include an elevated model baseline: its root-to-head clearance can
        // exceed the model's own height. Round outwards before publishing.
        var radiusValue = (float)Math.Sqrt(x * x + z * z);
        var heightValue = (float)Math.Max(high - low, high);
        if (radiusValue <= 0 || heightValue <= 0) return false;
        var radius = MathF.BitIncrement(radiusValue);
        var height = MathF.BitIncrement(heightValue);
        var minimum = MathF.BitDecrement((float)low);
        if (!float.IsFinite(radius) || !float.IsFinite(height) || !float.IsFinite(minimum)
            || radius <= 0 || height <= 0 || x == 0 && z == 0 || high <= low) return false;
        envelope = new(radius, height, minimum);
        return true;
    }

    private static bool TryScale(Vector3 scale, float first, float second, out Vector3 effective)
    {
        effective = default;
        if (!Finite(scale) || scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0
            || !float.IsFinite(first) || first <= 0 || !float.IsFinite(second) || second <= 0) return false;
        // Match the installed native skeleton update's float multiplication
        // order: draw axis * ScaleFactor2, then ScaleFactor1. A double product
        // could hide an intermediate native overflow or zero underflow.
        var intermediate = scale * second;
        if (!Finite(intermediate) || intermediate.X <= 0 || intermediate.Y <= 0 || intermediate.Z <= 0) return false;
        effective = intermediate * first;
        if (Finite(effective) && effective.X > 0 && effective.Y > 0 && effective.Z > 0) return true;
        effective = default;
        return false;
    }

    private static bool ValidBounds(Vector3 min, Vector3 max) => Finite(min) && Finite(max)
        && min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z;
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
