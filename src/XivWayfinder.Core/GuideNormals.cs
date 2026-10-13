using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>Zero represents an unavailable normal, allowing the renderer to use the current face.</summary>
internal static class GuideNormals
{
    public static Vector3 Normalize(Vector3 normal)
    {
        var largest = MathF.Max(MathF.Abs(normal.X), MathF.Max(MathF.Abs(normal.Y), MathF.Abs(normal.Z)));
        if (!float.IsFinite(largest) || largest == 0) return Vector3.Zero;
        normal /= largest;
        return Vector3.Normalize(normal);
    }
}

/// <summary>Inverse transpose of the linear transform, up to a positive scale. Translation is ignored.</summary>
internal readonly struct GuideNormalTransform
{
    private readonly Vector3 x, y, z;
    public GuideNormalTransform(Matrix4x4 matrix)
    {
        var a = new Vector3(matrix.M11, matrix.M12, matrix.M13);
        var b = new Vector3(matrix.M21, matrix.M22, matrix.M23);
        var c = new Vector3(matrix.M31, matrix.M32, matrix.M33);
        var magnitude = Vector3.Max(Vector3.Abs(a), Vector3.Max(Vector3.Abs(b), Vector3.Abs(c)));
        var largest = MathF.Max(magnitude.X, MathF.Max(magnitude.Y, magnitude.Z));
        x = y = z = Vector3.Zero;
        if (!float.IsFinite(largest) || largest == 0) return;
        // Dividing all axes by the same positive value preserves normal direction and
        // prevents large authored transforms from overflowing the cofactor products.
        a /= largest; b /= largest; c /= largest;
        var cx = Vector3.Cross(b, c); var cy = Vector3.Cross(c, a); var cz = Vector3.Cross(a, b);
        var determinant = Vector3.Dot(a, cx);
        if (determinant == 0 || !float.IsFinite(determinant)) return;
        var sign = MathF.CopySign(1, determinant);
        x = cx * sign; y = cy * sign; z = cz * sign;
    }
    public Vector3 Apply(Vector3 normal) => GuideNormals.Normalize(normal.X * x + normal.Y * y + normal.Z * z);
}
