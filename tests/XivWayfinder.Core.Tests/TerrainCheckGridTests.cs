using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class TerrainCheckGridTests
{
    [Fact]
    public void RefinementHasAFiniteBudgetWithoutRepeatingCoarseLocations()
    {
        Assert.Equal(1220, TerrainCheckGrid.Coarse.Count);
        Assert.Equal(3660, TerrainCheckGrid.Refinement.Count);
        var cells = TerrainCheckGrid.Coarse.Concat(TerrainCheckGrid.Refinement).ToArray();
        Assert.Equal(4880, cells.Distinct().Count());
        Assert.All(cells, cell => Assert.InRange(
            new Vector2(cell.X, cell.Z).Length() * TerrainCheckGrid.Spacing, 1.75f, 10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefinementFindsRealNarrowSharpTreadMissedByTheCoarsePass(bool swapAxes)
    {
        var origin = new Vector3(-250.7845f, 16, 46.648163f);
        Vector3 P(float x, float y, float z) => origin + (swapAxes ? new Vector3(z, y, x) : new Vector3(x, y, z));
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        { triangles.Add((a, b, c)); triangles.Add((b, d, c)); }
        Quad(P(-12, 0, -12), P(12, 0, -12), P(-12, 0, 12), P(12, 0, 12));
        // A 0.2-yalm cap lies entirely between the old X=3 and X=3.5 rays.
        // The floor continues below it, so both old samples report the same Y.
        Quad(P(3.12f, .2f, -1), P(3.32f, .2f, -1), P(3.12f, .2f, 1), P(3.32f, .2f, 1));
        foreach (var x in new[] { 3.12f, 3.32f })
            Quad(P(x, 0, -1), P(x, .2f, -1), P(x, 0, 1), P(x, .2f, 1));

        TrailSurfaceHit Down((int X, int Z) cell)
        {
            var point = origin + new Vector3(cell.X * TerrainCheckGrid.Spacing, 1.55f, cell.Z * TerrainCheckGrid.Spacing);
            return Cast(triangles, point, -Vector3.UnitY, 3.1f)!.Value;
        }
        Assert.All(TerrainCheckGrid.Coarse.Select(Down), hit => Assert.InRange(hit.Point.Y, 15.99999f, 16.00001f));
        var topCell = swapAxes ? (0, 13) : (13, 0);
        var floorCell = swapAxes ? (0, 12) : (12, 0);
        Assert.Contains(topCell, TerrainCheckGrid.Refinement);
        Assert.Contains(floorCell, TerrainCheckGrid.Coarse);
        var upper = Down(topCell); var lower = Down(floorCell);
        Assert.InRange(upper.Point.Y - lower.Point.Y, .19999f, .20001f);
        Assert.True(Math.Abs(upper.Normal.Y) >= .92f && Math.Abs(lower.Normal.Y) >= .92f);

        var direction = upper.Point - lower.Point; direction.Y = 0;
        var length = direction.Length(); direction /= length;
        var midpointHeight = lower.Point with { Y = (lower.Point.Y + upper.Point.Y) * .5f };
        var riser = Cast(triangles, midpointHeight, direction, length);
        Assert.True(riser.HasValue);
        Assert.InRange(Vector3.Distance(riser.Value.Point, P(3.12f, .1f, 0)), 0, .00002f);
        Assert.Equal(0, riser.Value.Normal.Y);
        Assert.InRange(Math.Abs(Vector3.Dot(riser.Value.Normal, direction)), .99999f, 1);
    }

    // Independent Moller–Trumbore intersections against explicit cap, floor and
    // riser triangles; no sampled height function invents the missing feature.
    private static TrailSurfaceHit? Cast(IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> triangles,
        Vector3 origin, Vector3 direction, float distance)
    {
        TrailSurfaceHit? nearest = null;
        foreach (var (a, b, c) in triangles)
        {
            var e1 = b - a; var e2 = c - a; var h = Vector3.Cross(direction, e2); var determinant = Vector3.Dot(e1, h);
            if (Math.Abs(determinant) < 1e-8f) continue;
            var inverse = 1 / determinant; var s = origin - a; var u = inverse * Vector3.Dot(s, h);
            if (u < -1e-6f || u > 1 + 1e-6f) continue;
            var q = Vector3.Cross(s, e1); var v = inverse * Vector3.Dot(direction, q);
            if (v < -1e-6f || u + v > 1 + 1e-6f) continue;
            var length = inverse * Vector3.Dot(e2, q);
            if (length < 0 || length > distance) continue;
            distance = length;
            nearest = new(origin + direction * length, Vector3.Normalize(Vector3.Cross(e1, e2)), a, b, c);
        }
        return nearest;
    }
}
