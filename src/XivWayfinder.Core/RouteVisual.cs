using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>Distances use the same flat route arc as the guide/trail; positions retain navmesh height.</summary>
public sealed record RouteVisual(float Progress, float GuideArc, float LookAheadArc,
    Vector3 GuidePosition, Vector3 LookAheadPosition, Vector4 Color)
{
    public static RouteVisual? Create(ReadOnlySpan<Vector3> path, Vector3 player,
        float guideDistance, float lookAheadDistance, Vector4 color, float? progressArc = null)
    {
        if (path.Length is < 2 or > RouteSnapshot.MaxPoints || !Plausible(player) ||
            !float.IsFinite(guideDistance) || !float.IsFinite(lookAheadDistance) ||
            guideDistance is < 0 or > 40 || lookAheadDistance is < 0 or > 40 ||
            !float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z) || !float.IsFinite(color.W)) return null;
        foreach (var point in path) if (!Plausible(point)) return null;
        var length = PathTrail.Length(path);
        if (progressArc is { } supplied && (!float.IsFinite(supplied) || supplied < 0 || supplied > length)) return null;
        var progress = progressArc ?? Math.Clamp(PathTrail.Project(path, player, out _), 0, length);
        var guide = Math.Min(length, progress + guideDistance);
        var ahead = Math.Min(length, progress + lookAheadDistance);
        return FromArcs(path, progress, guide, ahead, color);
    }

    /// <summary>Exports an independent companion arc, including a bounded loyal return behind the player.</summary>
    public static RouteVisual? FromArcs(ReadOnlySpan<Vector3> path, float progress, float guideArc, float lookAheadArc, Vector4 color)
    {
        if (path.Length is < 2 or > RouteSnapshot.MaxPoints || !float.IsFinite(progress) ||
            !float.IsFinite(guideArc) || !float.IsFinite(lookAheadArc) ||
            !float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z) || !float.IsFinite(color.W)) return null;
        foreach (var point in path) if (!Plausible(point)) return null;
        var length = PathTrail.Length(path);
        if (progress < 0 || progress > length || guideArc < 0 || guideArc > length || lookAheadArc < 0 || lookAheadArc > length
            || MathF.Abs(guideArc - progress) > 40 || MathF.Abs(lookAheadArc - guideArc) > 40) return null;
        return new(progress, guideArc, lookAheadArc, PathTrail.At(path, guideArc), PathTrail.At(path, lookAheadArc),
            Vector4.Clamp(color, Vector4.Zero, Vector4.One));
    }

    private static bool Plausible(Vector3 p) => MapCoords.IsPlausibleWorldCoord(p.X) &&
        MapCoords.IsPlausibleWorldCoord(p.Y) && MapCoords.IsPlausibleWorldCoord(p.Z);
}
