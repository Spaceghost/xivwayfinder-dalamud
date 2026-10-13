using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>Shrinks an oversized native preview using its scaled model extents.</summary>
public static class GuidePreviewFit
{
    public const float MinimumScale = .01f;
    public const float MaximumScale = 4f;
    public const float MaximumExtent = 1.5f;

    /// <summary>Native character bounds include translation, but their extents
    /// omit the draw scale and both native character scale factors.</summary>
    public static bool TryMeasureNativeBounds(Vector3 min, Vector3 max, Vector3 drawScale,
        float firstScaleFactor, float secondScaleFactor, out Vector3 worldSize) =>
        GuideNativeBounds.TryMeasureSize(min, max, drawScale, firstScaleFactor, secondScaleFactor, out worldSize);

    private static bool Finite(Vector3 value) => float.IsFinite(value.X)
        && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static bool TryFit(float currentScale, Vector3 worldSize, out float fittedScale)
    {
        fittedScale = currentScale;
        if (!float.IsFinite(currentScale) || currentScale < MinimumScale || currentScale > MaximumScale
            || !float.IsFinite(worldSize.X) || !float.IsFinite(worldSize.Y) || !float.IsFinite(worldSize.Z)
            || worldSize.X < 0 || worldSize.Y < 0 || worldSize.Z < 0)
            return false;
        var extent = Math.Max(worldSize.X, Math.Max(worldSize.Y, worldSize.Z));
        if (extent <= .0001f) return false;
        if (extent > MaximumExtent)
            fittedScale = Math.Max(MinimumScale, currentScale * (MaximumExtent / extent));
        return true;
    }
}
