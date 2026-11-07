using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>World geometry for depth-tested trail marks. Ground marks follow
/// the measured surface plane; floating marks keep their full extent above
/// the requested altitude, including the glow around their bright center.</summary>
public static class TrailGeometry
{
    public const float MaximumRadius = .6f;

    public static bool TryFrame(Vector3 position, Vector3 eye, Vector3 groundNormal,
        out Vector3 right, out Vector3 up, out Vector3 normal)
    {
        right = up = normal = default;
        if (!TrailPlacement.IsFinite(position) || !TrailPlacement.IsFinite(eye)
            || !TrailPlacement.IsFinite(groundNormal)) return false;
        normal = groundNormal == Vector3.Zero ? eye - position : groundNormal;
        if (!Normalize(ref normal)) return false;
        right = groundNormal == Vector3.Zero
            ? Vector3.Cross(Vector3.UnitY, normal) : Vector3.Cross(normal, eye - position);
        if (right.LengthSquared() < .0001f) right = Vector3.Cross(Vector3.UnitX, normal);
        if (!Normalize(ref right)) return false;
        up = Vector3.Cross(normal, right);
        return Normalize(ref up);
    }

    public static bool TryRibbonSide(Vector3 position, Vector3 along, Vector3 eye,
        Vector3 groundNormal, out Vector3 side)
    {
        side = default;
        if (!TrailPlacement.IsFinite(position) || !TrailPlacement.IsFinite(along)
            || !TrailPlacement.IsFinite(eye) || !TrailPlacement.IsFinite(groundNormal)) return false;
        var normal = groundNormal == Vector3.Zero ? eye - position : groundNormal;
        side = Vector3.Cross(normal, along);
        return Normalize(ref side);
    }

    /// <summary>Projection derivative gives the same apparent radius in pixels
    /// at either endpoint of a perspective ribbon, bounded near grazing views.</summary>
    public static float RadiusForPixels(Vector3 position, Vector3 axis, float pixels,
        Matrix4x4 viewProjection, Vector2 viewport)
    {
        if (!TrailPlacement.IsFinite(position) || !TrailPlacement.IsFinite(axis)
            || !float.IsFinite(pixels) || pixels <= 0 || !float.IsFinite(viewport.X)
            || !float.IsFinite(viewport.Y) || viewport.X <= 0 || viewport.Y <= 0) return 0;
        var p = Vector4.Transform(new Vector4(position, 1), viewProjection);
        var d = Vector4.Transform(new Vector4(axis, 0), viewProjection);
        if (!float.IsFinite(p.W) || p.W <= .0001f) return 0;
        var derivative = new Vector2(d.X * p.W - p.X * d.W, d.Y * p.W - p.Y * d.W)
            / (p.W * p.W) * viewport * .5f;
        var density = derivative.Length();
        if (!float.IsFinite(density) || density < .0001f) return 0;
        return Math.Min(MaximumRadius, pixels / density);
    }

    /// <summary>Screen center and major/minor diameters of a small world-space
    /// disc. Uses projection derivatives, including perspective foreshortening.</summary>
    public static bool TryProjectedDisc(Vector3 position, Vector3 right, Vector3 up, float radius,
        Matrix4x4 viewProjection, Vector2 viewport, out Vector2 screen, out Vector2 diameters)
    {
        screen = diameters = default;
        if (!TrailPlacement.IsFinite(position) || !TrailPlacement.IsFinite(right) || !TrailPlacement.IsFinite(up)
            || !float.IsFinite(radius) || radius <= 0 || !float.IsFinite(viewport.X) || !float.IsFinite(viewport.Y)
            || viewport.X <= 0 || viewport.Y <= 0) return false;
        var p = Vector4.Transform(new Vector4(position, 1), viewProjection);
        if (!float.IsFinite(p.W) || p.W <= .0001f) return false;
        var dx = Vector4.Transform(new Vector4(right, 0), viewProjection);
        var dy = Vector4.Transform(new Vector4(up, 0), viewProjection);
        var scale = viewport * new Vector2(.5f, -.5f) / (p.W * p.W);
        var x = new Vector2(dx.X * p.W - p.X * dx.W, dx.Y * p.W - p.Y * dx.W) * scale;
        var y = new Vector2(dy.X * p.W - p.X * dy.W, dy.Y * p.W - p.Y * dy.W) * scale;
        var xx = (double)x.LengthSquared(); var yy = (double)y.LengthSquared(); var xy = (double)Vector2.Dot(x, y);
        var discriminant = Math.Sqrt(Math.Max(0, (xx - yy) * (xx - yy) + 4 * xy * xy));
        var major = (xx + yy + discriminant) * .5;
        var minor = major > 0 ? Math.Max(0, xx * yy - xy * xy) / major : 0;
        screen = new((p.X / p.W + 1) * viewport.X * .5f, (1 - p.Y / p.W) * viewport.Y * .5f);
        diameters = new((float)Math.Sqrt(major) * radius * 2, (float)Math.Sqrt(minor) * radius * 2);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y) && float.IsFinite(diameters.X) && float.IsFinite(diameters.Y);
    }

    public static Vector3 AboveAltitude(Vector3 at, Vector3 right, Vector3 up, float radius)
        => at + Vector3.UnitY * (MathF.Sqrt(right.Y * right.Y + up.Y * up.Y) * radius);

    private static bool Normalize(ref Vector3 vector)
    {
        var length = vector.LengthSquared();
        if (!float.IsFinite(length) || length < 1e-10f) return false;
        vector /= MathF.Sqrt(length);
        return true;
    }
}
