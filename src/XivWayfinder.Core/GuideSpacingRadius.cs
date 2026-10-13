using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>
/// Retains the largest measured horizontal body radius for one asset and effective
/// scale. This is an observed envelope, not a bound on unsampled animation.
/// Owners reset it when selecting a different asset; scale changes reset it here.
/// </summary>
public sealed class GuideSpacingRadius
{
    private Vector3 scaleKey;
    private float retained;

    public void Reset() { scaleKey = default; retained = 0; }

    /// <summary>Measure only rendered vertices, around the authored model root.
    /// Include the bind pose so an initially narrow idle frame cannot erase the
    /// model's normal breadth. Include the current root rotation when tilted.</summary>
    public bool TryMeasureImported(ReadOnlySpan<Vector3> bindPose, ReadOnlySpan<Vector3> currentPose,
        ReadOnlySpan<int> indices, float scale, out float radius, Quaternion? rotation = null)
    {
        radius = 0;
        var orientation = rotation ?? Quaternion.Identity;
        if (!float.IsFinite(scale) || scale <= 0 || indices.IsEmpty || bindPose.IsEmpty
            || !currentPose.IsEmpty && currentPose.Length != bindPose.Length || !ValidRotation(orientation)) return false;
        double largestSquared = 0;
        foreach (var index in indices)
        {
            if ((uint)index >= (uint)bindPose.Length || !Include(bindPose[index])) return false;
            if (!currentPose.IsEmpty && !Include(currentPose[index])) return false;
        }
        var measured = (float)(Math.Sqrt(largestSquared) * scale);
        // The padding below exceeds any float rounding in the measurement.
        return TryRetain(measured, new Vector3(scale), out radius);

        bool Include(Vector3 point)
        {
            if (!Finite(point)) return false;
            if (orientation != Quaternion.Identity) point = Vector3.Transform(point, orientation);
            if (!Finite(point)) return false;
            var squared = (double)point.X * point.X + (double)point.Z * point.Z;
            largestSquared = Math.Max(largestSquared, squared);
            return true;
        }
    }

    /// <summary>Enclose a rotated native resource cylinder. Its horizontal circle
    /// projects to a radius no larger than R; tilted root-relative height can add
    /// at most |Y| times the horizontal component of the rotated up direction.</summary>
    public static bool TryMeasureRotatedNative(GuideNativeEnvelope envelope, Quaternion rotation, out float radius)
    {
        radius = 0;
        if (!float.IsFinite(envelope.Radius) || envelope.Radius <= 0
            || !float.IsFinite(envelope.Height) || envelope.Height <= 0
            || !float.IsFinite(envelope.MinimumY) || !ValidRotation(rotation)) return false;
        // Height covers both the full Y span and any elevated root-to-head gap.
        // This expression bounds the original high endpoint for either sign of minY.
        var high = (double)envelope.Height + Math.Min(0, envelope.MinimumY);
        var vertical = Math.Max(Math.Abs((double)envelope.MinimumY), Math.Abs(high));
        var up = Vector3.Transform(Vector3.UnitY, rotation);
        var horizontalUp = Math.Sqrt((double)up.X * up.X + (double)up.Z * up.Z);
        var measured = (float)(envelope.Radius + vertical * horizontalUp);
        if (!float.IsFinite(measured) || measured <= 0) return false;
        radius = measured;
        return true;
    }

    /// <summary>Add ten percent plus 0.05 yalm, round outwards in 0.05-yalm
    /// steps, then retain the largest observation for this effective scale.
    /// Invalid observations never replace a previously valid measurement.</summary>
    public bool TryRetain(float measuredRadius, Vector3 effectiveScale, out float radius)
    {
        radius = 0;
        if (!float.IsFinite(measuredRadius) || measuredRadius < 0 || !Finite(effectiveScale)
            || effectiveScale.X <= 0 || effectiveScale.Y <= 0 || effectiveScale.Z <= 0) return false;
        // Work in twentieths directly: avoids applying an inexact 1.1 float
        // before rounding, and cannot round a published value inward.
        var rounded = Math.Ceiling((double)measuredRadius * 22 + 1) / 20;
        var padded = (float)rounded;
        if (padded < rounded) padded = MathF.BitIncrement(padded);
        if (!float.IsFinite(padded) || padded <= 0) return false;
        if (effectiveScale != scaleKey) { scaleKey = effectiveScale; retained = 0; }
        retained = Math.Max(retained, padded);
        radius = retained;
        return true;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X)
        && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool ValidRotation(Quaternion value) => float.IsFinite(value.X) && float.IsFinite(value.Y)
        && float.IsFinite(value.Z) && float.IsFinite(value.W) && Math.Abs(value.LengthSquared() - 1) <= .0001f;
}
