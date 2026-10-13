using System.Numerics;

namespace XivWayfinder.Core;

public readonly record struct GuideBoardingSweepSphere(Vector3 From, Vector3 To, float Radius);

/// <summary>Adaptive conservative cover of every visible triangle and the
/// convex interpolation between two exact poses. Spheres stay above a proved
/// flat support plane; native queries therefore need no floor-normal exclusion.
/// This does not assert clearance: all returned spheres must be checked against
/// all game collision faces. Failure produces no partial approval.</summary>
public static class GuideBoardingMeshCover
{
    public const int MaximumSpheres = 128;
    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, Vector3 D, Vector3 E, Vector3 F)
    {
        public Vector3 BeforeMin => Vector3.Min(Vector3.Min(A, B), C);
        public Vector3 BeforeMax => Vector3.Max(Vector3.Max(A, B), C);
        public Vector3 AfterMin => Vector3.Min(Vector3.Min(D, E), F);
        public Vector3 AfterMax => Vector3.Max(Vector3.Max(D, E), F);
    }
    public static bool TryCreate(ReadOnlySpan<Vector3> before, ReadOnlySpan<Vector3> after, ReadOnlySpan<int> indices,
        float supportY, out GuideBoardingSweepSphere[] spheres)
    {
        spheres = [];
        if (before.Length != after.Length || before.IsEmpty || before.Length > 200000 || indices.Length % 3 != 0
            || indices.Length is < 3 or > 60000 || !float.IsFinite(supportY)) return false;
        var faces = new Face[indices.Length / 3];
        for (var i = 0; i < indices.Length; i += 3)
        {
            var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
            if ((uint)a >= before.Length || (uint)b >= before.Length || (uint)c >= before.Length) return false;
            faces[i / 3] = new(before[a], before[b], before[c], after[a], after[b], after[c]);
        }
        var result = new List<GuideBoardingSweepSphere>();
        if (!Cover(faces, 0)) return false;
        spheres = result.ToArray(); return true;

        bool Cover(Span<Face> group, int depth)
        {
            if (depth > 18 || result.Count >= MaximumSpheres) return false;
            var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
            var nextMinimum = minimum; var nextMaximum = maximum;
            foreach (var face in group)
            {
                var low = face.BeforeMin; var high = face.BeforeMax; var nextLow = face.AfterMin; var nextHigh = face.AfterMax;
                if (!TrailPlacement.IsFinite(low) || !TrailPlacement.IsFinite(high) || !TrailPlacement.IsFinite(nextLow)
                    || !TrailPlacement.IsFinite(nextHigh) || Math.Min(low.Y, nextLow.Y) < supportY + .002f) return false;
                minimum = Vector3.Min(minimum, low); maximum = Vector3.Max(maximum, high);
                nextMinimum = Vector3.Min(nextMinimum, nextLow); nextMaximum = Vector3.Max(nextMaximum, nextHigh);
            }
            var center = (minimum + maximum) * .5f; var nextCenter = (nextMinimum + nextMaximum) * .5f;
            var radius = MathF.BitIncrement(Math.Max((maximum - minimum).Length(), (nextMaximum - nextMinimum).Length()) * .5f + .0001f);
            if (Math.Min(center.Y, nextCenter.Y) - radius >= supportY + .001f)
            { result.Add(new(center, nextCenter, radius)); return true; }
            if (group.Length > 1)
            {
                var size = maximum - minimum; var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Z >= size.Y ? 2 : 1;
                group.Sort((a, b) => Coordinate(a).CompareTo(Coordinate(b)));
                var split = group.Length / 2;
                return Cover(group[..split], depth + 1) && Cover(group[split..], depth + 1);
                float Coordinate(Face f) { var p = f.A + f.B + f.C + f.D + f.E + f.F; return axis == 0 ? p.X : axis == 1 ? p.Y : p.Z; }
            }
            var triangle = group[0];
            var ab = Math.Max(Vector3.DistanceSquared(triangle.A, triangle.B), Vector3.DistanceSquared(triangle.D, triangle.E));
            var bc = Math.Max(Vector3.DistanceSquared(triangle.B, triangle.C), Vector3.DistanceSquared(triangle.E, triangle.F));
            var ca = Math.Max(Vector3.DistanceSquared(triangle.C, triangle.A), Vector3.DistanceSquared(triangle.F, triangle.D));
            if (bc >= ab && bc >= ca) triangle = new(triangle.B, triangle.C, triangle.A, triangle.E, triangle.F, triangle.D);
            else if (ca >= ab) triangle = new(triangle.C, triangle.A, triangle.B, triangle.F, triangle.D, triangle.E);
            var m = (triangle.A + triangle.B) * .5f; var n = (triangle.D + triangle.E) * .5f;
            Span<Face> halves = stackalloc Face[2]
            {new(triangle.A,m,triangle.C,triangle.D,n,triangle.F),new(m,triangle.B,triangle.C,n,triangle.E,triangle.F)};
            return Cover(halves[..1], depth + 1) && Cover(halves[1..], depth + 1);
        }
    }
}
