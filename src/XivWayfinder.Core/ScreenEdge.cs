using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>
/// Where the pointer goes when the way to go is off screen: on the border of the view, inset by a margin, where a
/// ray from the character's screen position along the on-screen direction leaves it. Screen space is pixels with
/// +Y down, as ImGui and <c>WorldToScreen</c> use it.
/// </summary>
public static class ScreenEdge
{
    /// <summary>Whether <paramref name="p"/> lies inside the rectangle (edges included).</summary>
    public static bool Inside(Vector2 p, Vector2 min, Vector2 max) =>
        p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;

    /// <summary>The nearest point of the rectangle to <paramref name="p"/>.</summary>
    public static Vector2 Clamp(Vector2 p, Vector2 min, Vector2 max) =>
        new(Fit(p.X, min.X, MathF.Max(min.X, max.X)), Fit(p.Y, min.Y, MathF.Max(min.Y, max.Y)));

    /// <summary>
    /// Where a ray from <paramref name="origin"/> (clamped into the rectangle first) along <paramref name="dir"/>
    /// meets the rectangle's border. A zero or non-finite direction points straight down, towards the camera.
    /// </summary>
    public static Vector2 RayToEdge(Vector2 origin, Vector2 dir, Vector2 min, Vector2 max)
    {
        var o = Clamp(origin, min, max);
        var d = Normalize(dir);
        var t = float.PositiveInfinity;
        if (d.X > 1e-6f)
            t = MathF.Min(t, (max.X - o.X) / d.X);
        else if (d.X < -1e-6f)
            t = MathF.Min(t, (min.X - o.X) / d.X);
        if (d.Y > 1e-6f)
            t = MathF.Min(t, (max.Y - o.Y) / d.Y);
        else if (d.Y < -1e-6f)
            t = MathF.Min(t, (min.Y - o.Y) / d.Y);
        if (!float.IsFinite(t) || t < 0f)
            t = 0f;
        return Clamp(o + d * t, min, max);
    }

    /// <summary>The screen angle of a direction, radians, 0 = right, π/2 = down (screen +Y is down).</summary>
    public static float Angle(Vector2 dir)
    {
        var d = Normalize(dir);
        return MathF.Atan2(d.Y, d.X);
    }

    /// <summary>A unit vector, or straight down when <paramref name="v"/> has no direction.</summary>
    public static Vector2 Normalize(Vector2 v)
    {
        var len = v.Length();
        return len > 1e-6f && float.IsFinite(len) ? v / len : new Vector2(0f, 1f);
    }

    /// <summary>
    /// The on-screen direction of travel from two projections: the character's screen position and that of a
    /// point a little way ahead of it along the ground direction. Both are close to the character, so both are in
    /// front of the camera even when the target itself is behind it; that is why this, and not the target's own
    /// projection, orients the pointer. Returns false when the two land on the same pixel (looking straight down
    /// the direction), leaving <paramref name="dir"/> unchanged.
    /// </summary>
    public static bool Direction(Vector2 from, Vector2 ahead, ref Vector2 dir)
    {
        var v = ahead - from;
        var len = v.Length();
        if (len < 0.5f || !float.IsFinite(len))
            return false;
        dir = v / len;
        return true;
    }

    private static float Fit(float v, float lo, float hi) => float.IsNaN(v) ? lo : Math.Clamp(v, lo, hi);
}
