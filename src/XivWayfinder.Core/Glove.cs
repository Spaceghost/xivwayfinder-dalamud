using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>
/// A pointing hand drawn as a picture that points along +X: where its turning point (the palm) and its fingertip
/// sit, as fractions of the picture's box for a sprite, or in box-size units from the palm for a drawn one.
/// </summary>
public readonly record struct GloveModel(Vector2 Pivot, Vector2 Tip)
{
    /// <summary>The picture's own pointing angle, palm to fingertip (screen radians, +Y down).</summary>
    public float AxisAngle => MathF.Atan2(Tip.Y - Pivot.Y, Tip.X - Pivot.X);

    /// <summary>Palm to fingertip, in box sizes.</summary>
    public float Reach => Vector2.Distance(Tip, Pivot);
}

/// <summary>
/// The glove pointer. The picture is the game's own gamepad cursor, the white gloved hand, read from the player's
/// game files at run time; this repository ships no copy of it. Measured on game version 2026.09.15.0000.0000:
/// <c>ui/uld/Cursor.uld</c> names one texture, <c>ui/uld/Cursor.tex</c> (64×64, BC3; <c>ui/uld/Cursor_hr1.tex</c>
/// is 128×128), and one part, U 0 V 0 W 64 H 64, the whole texture. The hand points right; its palm centre is at
/// (0.48, 0.53) of the part and the index fingertip at (0.80, 0.445). When that texture is missing,
/// <see cref="VectorGlove"/> draws an original white glove in the same spirit.
/// </summary>
public static class Glove
{
    public const string UldPath = "ui/uld/Cursor.uld";
    public const string TexturePath = "ui/uld/Cursor.tex";
    public const string TextureHrPath = "ui/uld/Cursor_hr1.tex";

    /// <summary>The part rectangle measured in the game data, in the 1× texture's pixels.</summary>
    public static readonly (int U, int V, int W, int H) MeasuredPart = (0, 0, 64, 64);

    public static readonly GloveModel GameSprite = new(new Vector2(0.48f, 0.53f), new Vector2(0.80f, 0.445f));

    /// <summary>
    /// Whether to draw the hand mirrored, so the thumb stays on top when it points left instead of turning upside
    /// down. Flips past ±100° and back past ±80°, so a pointer near straight up or down does not flicker.
    /// </summary>
    public static bool Mirror(float angle, bool mirroredNow)
    {
        var c = MathF.Cos(angle);
        if (!float.IsFinite(c))
            return mirroredNow;
        if (c < -0.17f)
            return true;
        if (c > 0.17f)
            return false;
        return mirroredNow;
    }

    /// <summary>
    /// Where a point of the picture lands on screen: <paramref name="local"/> is measured from the pivot in box
    /// sizes; the pivot goes to <paramref name="anchor"/>, the box is <paramref name="size"/> pixels, and the
    /// picture turns so its own axis points along <paramref name="angle"/>.
    /// </summary>
    public static Vector2 Place(Vector2 local, float axisAngle, Vector2 anchor, float size, float angle, bool mirrored)
    {
        var l = local * size;
        var axis = axisAngle;
        if (mirrored)
        {
            l.X = -l.X;
            axis = MathF.PI - axisAngle;
        }

        var rot = angle - axis;
        var (s, c) = MathF.SinCos(rot);
        return anchor + new Vector2(l.X * c - l.Y * s, l.X * s + l.Y * c);
    }

    /// <summary>
    /// The four screen corners for <c>AddImageQuad</c>, in the picture's own order (top-left, top-right,
    /// bottom-right, bottom-left), so that its pivot sits on <paramref name="anchor"/> and its fingertip points
    /// along <paramref name="angle"/>. Mirroring moves the corners, so the texture coordinates never change.
    /// </summary>
    public static void Quad(GloveModel model, Vector2 anchor, float size, float angle, bool mirrored, Span<Vector2> corners)
    {
        if (corners.Length < 4)
            throw new ArgumentException("four corners", nameof(corners));
        var axis = model.AxisAngle;
        corners[0] = Place(new Vector2(0f, 0f) - model.Pivot, axis, anchor, size, angle, mirrored);
        corners[1] = Place(new Vector2(1f, 0f) - model.Pivot, axis, anchor, size, angle, mirrored);
        corners[2] = Place(new Vector2(1f, 1f) - model.Pivot, axis, anchor, size, angle, mirrored);
        corners[3] = Place(new Vector2(0f, 1f) - model.Pivot, axis, anchor, size, angle, mirrored);
    }

    /// <summary>Where the fingertip lands for the same placement.</summary>
    public static Vector2 TipOnScreen(GloveModel model, Vector2 anchor, float size, float angle) =>
        anchor + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (model.Reach * size);

    /// <summary>
    /// Texture coordinates of a ULD part. <paramref name="u"/>..<paramref name="h"/> are in the 1× texture's pixels
    /// as the ULD stores them; <paramref name="scale"/> is 2 for an _hr1 texture. Clamped to the texture.
    /// </summary>
    public static (Vector2 Min, Vector2 Max) PartUv(int u, int v, int w, int h, int textureWidth, int textureHeight, int scale)
    {
        if (textureWidth <= 0 || textureHeight <= 0 || scale <= 0)
            return (Vector2.Zero, Vector2.One);
        var min = new Vector2(Math.Clamp(u * scale, 0, textureWidth) / (float)textureWidth, Math.Clamp(v * scale, 0, textureHeight) / (float)textureHeight);
        var max = new Vector2(Math.Clamp((u + w) * scale, 0, textureWidth) / (float)textureWidth, Math.Clamp((v + h) * scale, 0, textureHeight) / (float)textureHeight);
        if (max.X <= min.X || max.Y <= min.Y)
            return (Vector2.Zero, Vector2.One);
        return (min, max);
    }
}

/// <summary>
/// The fallback glove: an original cartoon hand made of convex shapes (cuff, palm, three curled fingers, thumb,
/// pointing finger), drawn back to front, each filled white and outlined in ink. Units are box sizes from the palm,
/// pointing along +X with +Y down. No pixels are copied from anywhere.
/// </summary>
public static class VectorGlove
{
    public static readonly GloveModel Model = new(Vector2.Zero, new Vector2(0.715f, -0.06f));

    /// <summary>The shapes, back to front. Every one is convex, so ImGui can fill it directly.</summary>
    public static readonly Vector2[][] Shapes =
    [
        RoundedRect(new Vector2(-0.52f, 0.06f), new Vector2(0.10f, 0.27f), 0.07f, 5),      // cuff
        Ellipse(new Vector2(-0.17f, 0.08f), new Vector2(0.33f, 0.28f), 0f, 28),             // palm
        Ellipse(new Vector2(0.02f, 0.33f), new Vector2(0.12f, 0.07f), 0f, 16),              // little finger
        Ellipse(new Vector2(0.09f, 0.23f), new Vector2(0.15f, 0.08f), 0f, 18),              // ring finger
        Ellipse(new Vector2(0.12f, 0.10f), new Vector2(0.17f, 0.085f), 0f, 18),             // middle finger
        Ellipse(new Vector2(-0.10f, -0.17f), new Vector2(0.19f, 0.085f), -0.55f, 18),       // thumb
        Capsule(new Vector2(0.02f, -0.06f), new Vector2(0.62f, -0.06f), 0.095f, 10),        // pointing finger
    ];

    public static Vector2[] Ellipse(Vector2 centre, Vector2 radii, float tilt, int points)
    {
        var result = new Vector2[points];
        var (ts, tc) = MathF.SinCos(tilt);
        for (var i = 0; i < points; i++)
        {
            var (s, c) = MathF.SinCos(i * Heading.Tau / points);
            var p = new Vector2(c * radii.X, s * radii.Y);
            result[i] = centre + new Vector2(p.X * tc - p.Y * ts, p.X * ts + p.Y * tc);
        }

        return result;
    }

    /// <summary>A stadium from <paramref name="a"/> to <paramref name="b"/>: two half circles joined by straight sides.</summary>
    public static Vector2[] Capsule(Vector2 a, Vector2 b, float radius, int pointsPerEnd)
    {
        var axis = MathF.Atan2(b.Y - a.Y, b.X - a.X);
        var result = new Vector2[(pointsPerEnd + 1) * 2];
        var n = 0;
        for (var i = 0; i <= pointsPerEnd; i++)
        {
            var t = axis - MathF.PI / 2 + MathF.PI * i / pointsPerEnd; // around the far end
            result[n++] = b + new Vector2(MathF.Cos(t), MathF.Sin(t)) * radius;
        }

        for (var i = 0; i <= pointsPerEnd; i++)
        {
            var t = axis + MathF.PI / 2 + MathF.PI * i / pointsPerEnd; // around the near end
            result[n++] = a + new Vector2(MathF.Cos(t), MathF.Sin(t)) * radius;
        }

        return result;
    }

    public static Vector2[] RoundedRect(Vector2 centre, Vector2 half, float radius, int pointsPerCorner)
    {
        var r = MathF.Min(radius, MathF.Min(half.X, half.Y));
        var inner = half - new Vector2(r, r);
        var result = new Vector2[(pointsPerCorner + 1) * 4];
        var n = 0;
        Vector2[] corners = [new(inner.X, inner.Y), new(-inner.X, inner.Y), new(-inner.X, -inner.Y), new(inner.X, -inner.Y)];
        for (var k = 0; k < 4; k++)
        {
            for (var i = 0; i <= pointsPerCorner; i++)
            {
                var t = k * MathF.PI / 2 + MathF.PI / 2 * i / pointsPerCorner;
                result[n++] = centre + corners[k] + new Vector2(MathF.Cos(t), MathF.Sin(t)) * r;
            }
        }

        return result;
    }
}
