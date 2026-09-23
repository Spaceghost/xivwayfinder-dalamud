using System.Numerics;

namespace Wayfinder.Core;

/// <summary>
/// Directions on the ground plane. The game's world is Y-up and a character's <c>Rotation</c> is 0 when it faces
/// +Z, so its forward vector on the (X, Z) plane is (sin r, cos r). Everything here works in that plane: height
/// never changes which way to walk.
/// </summary>
public static class Heading
{
    public const float Tau = MathF.PI * 2f;

    /// <summary>The (X, Z) unit vector a character with this rotation faces.</summary>
    public static Vector2 Forward(float rotation) => new(MathF.Sin(rotation), MathF.Cos(rotation));

    /// <summary>The character rotation that faces along <paramref name="dir"/> (X, Z); 0 for a zero vector.</summary>
    public static float RotationOf(Vector2 dir) => dir == Vector2.Zero ? 0f : MathF.Atan2(dir.X, dir.Y);

    /// <summary>From one world position to another on the ground plane, as (dX, dZ).</summary>
    public static Vector2 Flat(Vector3 from, Vector3 to) => new(to.X - from.X, to.Z - from.Z);

    /// <summary>Ground distance in yalms, ignoring height.</summary>
    public static float FlatDistance(Vector3 a, Vector3 b) => Flat(a, b).Length();

    /// <summary>The unit (X, Z) direction from one position to another; zero when they share a spot.</summary>
    public static Vector2 DirectionTo(Vector3 from, Vector3 to)
    {
        var d = Flat(from, to);
        var len = d.Length();
        return len < 1e-4f || !float.IsFinite(len) ? Vector2.Zero : d / len;
    }

    /// <summary>An angle folded into (-π, π].</summary>
    public static float Wrap(float angle)
    {
        if (!float.IsFinite(angle))
            return 0f;
        angle %= Tau;
        if (angle <= -MathF.PI)
            angle += Tau;
        else if (angle > MathF.PI)
            angle -= Tau;
        return angle;
    }

    /// <summary>How far apart two rotations are, 0..π, whichever way round is shorter.</summary>
    public static float Between(float a, float b) => MathF.Abs(Wrap(a - b));

    /// <summary>
    /// 0 when a character with <paramref name="rotation"/> faces along <paramref name="dir"/>, 1 when it faces the
    /// opposite way, (1 − cos θ) / 2 in between: smooth, and already 0.5 at a right angle.
    /// </summary>
    public static float TurnAway(float rotation, Vector2 dir)
    {
        if (dir == Vector2.Zero)
            return 0f;
        var cos = Vector2.Dot(Forward(rotation), Vector2.Normalize(dir));
        return Math.Clamp((1f - cos) * 0.5f, 0f, 1f);
    }

    /// <summary>A point <paramref name="distance"/> yalms from <paramref name="origin"/> along <paramref name="dir"/>, <paramref name="height"/> above it.</summary>
    public static Vector3 Ahead(Vector3 origin, Vector2 dir, float distance, float height) =>
        new(origin.X + dir.X * distance, origin.Y + height, origin.Z + dir.Y * distance);
}
