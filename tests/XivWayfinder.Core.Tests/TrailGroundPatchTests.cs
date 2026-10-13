using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class TrailGroundPatchTests(Xunit.ITestOutputHelper testLog)
{
    private static TrailGroundVertex V(float x, float z, float y = 0) => new(new(x, y + TrailPlacement.GroundClearance, z), new(x + 1, z + 1, .5f, .8f));
    private static readonly TrailGroundVertex[] Footprint = [V(-.45f, -.35f), V(.48f, -.25f), V(.1f, .45f)];

    [Fact]
    public void NeighboringTessellationFacesPreserveEntireFootprintAndGlow()
    {
        var terrain = Terrain.Grid(8, .2f, (_, _) => 0);
        var mesh = Project(terrain, Footprint);
        AssertArea(Footprint, mesh);
        AssertOnTerrain(terrain, mesh);
        foreach (var v in mesh)
        {
            Assert.InRange(Math.Abs(v.Color.X - (v.Position.X + 1)), 0, .00002f);
            Assert.InRange(Math.Abs(v.Color.Y - (v.Position.Z + 1)), 0, .00002f);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedAndRidgedMeshesKeepWholeTrianglesAboveActualTerrain(bool ridge)
    {
        var terrain = Terrain.Grid(10, .16f, (x, z) => ridge
            ? .28f * MathF.Max(0, 1 - Math.Abs(x) / .23f)
            : .2f * MathF.Cos(x * 3) * MathF.Cos(z * 3));
        var mesh = Project(terrain, Footprint);
        AssertArea(Footprint, mesh);
        AssertOnTerrain(terrain, mesh);
    }

    [Fact]
    public void SmallMarksRemainCoveredFarFromWorldOrigin()
    {
        var terrain = Terrain.Grid(8, .2f, (x, z) => .1f * MathF.Cos(x * 3) * MathF.Cos(z * 3));
        var offset = new Vector3(1000, 40, -800);
        for (var i = 0; i < terrain.Faces.Count; i++)
        { var (a, b, c) = terrain.Faces[i]; terrain.Faces[i] = (a + offset, b + offset, c + offset); }
        var source = Footprint.Select(v => v with { Position = v.Position + offset }).ToArray();
        var result = Project(terrain, source);
        AssertArea(source, result);
        for (var i = 0; i < result.Length; i++)
        {
            var p = result[i].Position;
            var hit = terrain.Cast(p + Vector3.UnitY, -Vector3.UnitY, 2);
            Assert.True(hit.HasValue);
            Assert.InRange(p.Y - hit.Value.Point.Y, TrailPlacement.GroundClearance - .0002f, TrailPlacement.GroundClearance + .0002f);
        }
    }

    [Fact]
    public void StepCrossingSplitsAtRiserInsteadOfDrawingThroughTread()
    {
        var terrain = new Terrain();
        terrain.Quad(-1, 0, -1, 1, 0); terrain.Quad(0, 1, -1, 1, .3f);
        // The vertical riser is real geometry too; downward rays cannot mistake it for a floor.
        terrain.Faces.Add((new(0, 0, -1), new(0, .3f, -1), new(0, .3f, 1)));
        terrain.Faces.Add((new(0, 0, -1), new(0, .3f, 1), new(0, 0, 1)));
        var mesh = Project(terrain, Footprint);
        AssertArea(Footprint, mesh); AssertOnTerrain(terrain, mesh);
        for (var i = 0; i < mesh.Length; i += 3)
        {
            var min = Math.Min(mesh[i].Position.X, Math.Min(mesh[i + 1].Position.X, mesh[i + 2].Position.X));
            var max = Math.Max(mesh[i].Position.X, Math.Max(mesh[i + 1].Position.X, mesh[i + 2].Position.X));
            Assert.True(min >= -.00001f || max <= .00001f, "An emitted triangle crossed the riser.");
        }
    }

    [Fact]
    public void RaisedTreadIsDiscoveredEvenWhenLowerFloorCoversItsFootprint()
    {
        var terrain = new Terrain(); terrain.Quad(-10, 10, -10, 10, 0); terrain.Quad(0, 1, -1, 1, .3f);
        TrailGroundVertex[] source = [V(-.2f, -.2f), V(.2f, -.2f), V(-.2f, .2f)];
        var mesh = Project(terrain, source);
        AssertArea(source, mesh); AssertOnTerrain(terrain, mesh);
        Assert.Contains(mesh, v => v.Position.Y > .4f);
    }

    [Theory]
    [InlineData(.75f)]
    [InlineData(1.25f)]
    public void TallPartialTreadCannotHideAboveTheWitnessOrigins(float height)
    {
        var terrain = new Terrain(); terrain.Quad(-10, 10, -10, 10, 0); terrain.Quad(0, 1, -1, 1, height);
        TrailGroundVertex[] source = [V(-.2f, -.2f), V(.2f, -.2f), V(-.2f, .2f)];
        var patch = new TrailGroundPatch(terrain.Cast, () => 0); patch.Begin(1, 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        // With the old short rays this succeeded on the lower floor, and six
        // independent interior samples lay inside the raised tread. Above the
        // route's support band, layer assignment is ambiguous; emit no primitive.
        Assert.False(patch.TryProject(source[0], source[1], source[2], output, out var count));
        Assert.Equal(0, count);
        Assert.Equal("partial higher surface over route", patch.LastFailure);
        Assert.InRange(patch.Queries, 1, TrailGroundPatch.MaximumQueries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullFairyGlowDistinguishesDeckFromTallTreadAlongFanSeam(bool fullDeck)
    {
        var terrain = new Terrain(); terrain.Quad(-10, 10, -10, 10, 0);
        terrain.Quad(fullDeck ? -1 : 0, 1, -1, 1, .75f);
        var patch = new TrailGroundPatch(terrain.Cast, () => 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        // The boundary X=0 exactly follows fan edges. Each individual triangle
        // is wholly on one side, so only a whole-primitive layer check detects it.
        TrailGroundVertex Point(int sector, float fraction)
        {
            var angle = sector * MathF.Tau / 12;
            var x = MathF.Cos(angle) * .12f * fraction;
            if (Math.Abs(x) < .000001f) x = 0;
            return V(x, MathF.Sin(angle) * .12f * fraction);
        }
        var triangles = new List<(TrailGroundVertex A, TrailGroundVertex B, TrailGroundVertex C)>();
        for (var sector = 0; sector < 12; sector++)
        {
            triangles.Add((V(0, 0), Point(sector, .16f), Point(sector + 1, .16f)));
            foreach (var (inner, outer) in new[] { (.16f, .48f), (.48f, 1f) })
            {
                triangles.Add((Point(sector, inner), Point(sector, outer), Point(sector + 1, outer)));
                triangles.Add((Point(sector, inner), Point(sector + 1, outer), Point(sector + 1, inner)));
            }
        }
        Assert.Equal(60, triangles.Count);
        // Check cold projection and retained geometry: cached triangles must
        // carry their layer evidence into the next whole-primitive decision.
        for (var frame = 0; frame < 2; frame++)
        {
            patch.Begin(1, frame / 60d); patch.BeginPrimitive();
            foreach (var (a, b, c) in triangles)
                Assert.True(patch.TryProject(a, b, c, output, out _), patch.LastFailure);
            Assert.Equal(fullDeck, patch.CompletePrimitive());
            if (!fullDeck) Assert.Equal("partial higher surface over primitive", patch.LastFailure);
            if (frame == 1) Assert.Equal(0, patch.Queries);
        }
    }

    [Fact]
    public void GapBetweenTreadsNeverGetsAnInventedBridge()
    {
        var terrain = new Terrain(); terrain.Quad(-1, -.08f, -1, 1, 0); terrain.Quad(.08f, 1, -1, 1, 0);
        var patch = new TrailGroundPatch(terrain.Cast, () => 0); patch.Begin(1, 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        Assert.False(patch.TryProject(Footprint[0], Footprint[1], Footprint[2], output, out var count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void StackedFloorsUseRouteLayerWithoutDuplicateLowerGeometry()
    {
        var terrain = new Terrain(); terrain.Quad(-2, 2, -2, 2, 0); terrain.Quad(-2, 2, -2, 2, 1);
        var patch = new TrailGroundPatch(terrain.Cast, () => 0); patch.Begin(1, 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        var complete = false; var n = 0;
        for (var frame = 0; frame < 4 && !complete; frame++)
        {
            patch.Begin(1, frame / 60d);
            complete = patch.TryProject(Footprint[0], Footprint[1], Footprint[2], output, out n);
            Assert.InRange(patch.Queries, 0, TrailGroundPatch.MaximumQueries);
        }
        Assert.True(complete);
        Assert.All(output[..n], v => Assert.Equal(TrailPlacement.GroundClearance, v.Position.Y));
        patch.Begin(1, .1);
        var upper = Footprint.Select(v => v with { Position = v.Position + Vector3.UnitY }).ToArray();
        Assert.True(patch.TryProject(upper[0], upper[1], upper[2], output, out n));
        AssertArea(upper, output[..n]);
        Assert.All(output[..n], v => Assert.Equal(1 + TrailPlacement.GroundClearance, v.Position.Y));
        // A later lower-floor primitive must still exclude the cached upper faces.
        Assert.True(patch.TryProject(Footprint[0], Footprint[1], Footprint[2], output, out n));
        Assert.All(output[..n], v => Assert.Equal(TrailPlacement.GroundClearance, v.Position.Y));
    }

    [Fact]
    public void FloorSamplerPrefersLowerRouteUnderDeckButStillRecoversApproximateTreadHeight()
    {
        var terrain = new Terrain(); terrain.Quad(-2, 2, -2, 2, 0); terrain.Quad(-2, 2, -2, 2, 1);
        var sampler = new TrailSurfaceSampler(terrain.Cast); sampler.Begin(1);
        Assert.True(sampler.TryPlace(Vector3.Zero, 0, out var lower));
        Assert.Equal(TrailPlacement.GroundClearance, lower.Y);
        Assert.True(sampler.TryPlace(Vector3.UnitY, 0, out var upper));
        Assert.Equal(1 + TrailPlacement.GroundClearance, upper.Y);
        var tread = new Terrain(); tread.Quad(-2, 2, -2, 2, .6f);
        sampler = new TrailSurfaceSampler(tread.Cast); sampler.Begin(1);
        Assert.True(sampler.TryPlace(Vector3.Zero, 0, out var corrected));
        Assert.Equal(.6f + TrailPlacement.GroundClearance, corrected.Y);
    }

    [Fact]
    public void QueryBudgetAllowsGradualWarmupAndDoesNotReuseExpiredOrOtherZoneFaces()
    {
        var terrain = Terrain.Grid(8, .2f, (_, _) => 0);
        var time = 0d;
        var patch = new TrailGroundPatch(terrain.Cast, () => time += .001);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        var complete = false;
        for (var frame = 0; frame < 16; frame++)
        {
            patch.Begin(1, frame / 60d);
            complete = patch.TryProject(Footprint[0], Footprint[1], Footprint[2], output, out _);
            Assert.InRange(patch.Queries, 0, 4);
            if (complete) break;
        }
        Assert.True(complete);
        terrain.Faces.Clear();
        patch.Begin(1, 1);
        Assert.False(patch.TryProject(Footprint[0], Footprint[1], Footprint[2], output, out var n)); Assert.Equal(0, n);
        terrain.Quad(-2, 2, -2, 2, 0);
        complete = false;
        for (var frame = 0; frame < 16; frame++)
        {
            patch.Begin(1, 2 + frame / 60d);
            complete = patch.TryProject(Footprint[0], Footprint[1], Footprint[2], output, out _);
            Assert.InRange(patch.Queries, 0, 4);
            if (complete) break;
        }
        Assert.True(complete);
        terrain.Faces.Clear(); patch.Begin(2, 2.3);
        Assert.False(patch.TryProject(Footprint[0], Footprint[1], Footprint[2], output, out n)); Assert.Equal(0, n);
    }

    [Theory]
    [InlineData(0, 0, .0675f)]
    [InlineData(-105.31f, -43.19f, .0675f)]
    [InlineData(-105.31f, -43.19f, .12f)]
    [InlineData(1000, -800, .0675f)]
    public void EveryTinyTriangleInFairyGlowSurvivesMeasuredGroundProjection(float x, float z, float radius)
    {
        var terrain = new Terrain(); terrain.Quad(x - 10, x + 10, z - 10, z + 10, 19.32f);
        var center = new Vector3(x, 19.32f + TrailPlacement.GroundClearance, z);
        var triangles = new List<TrailGroundVertex>();
        TrailGroundVertex Point(int sector, float fraction)
        {
            var angle = sector * MathF.Tau / 12;
            return new(center + new Vector3(MathF.Cos(angle), 0, -MathF.Sin(angle)) * radius * fraction,
                new Vector4(1, .8f, .3f, 1 - fraction));
        }
        for (var sector = 0; sector < 12; sector++)
        {
            triangles.Add(new(center, Vector4.One)); triangles.Add(Point(sector, .16f)); triangles.Add(Point(sector + 1, .16f));
            foreach (var (inner, outer) in new[] { (.16f, .48f), (.48f, 1f) })
            {
                triangles.Add(Point(sector, inner)); triangles.Add(Point(sector, outer)); triangles.Add(Point(sector + 1, outer));
                triangles.Add(Point(sector, inner)); triangles.Add(Point(sector + 1, outer)); triangles.Add(Point(sector + 1, inner));
            }
        }
        Assert.Equal(180, triangles.Count);
        var patch = new TrailGroundPatch(terrain.Cast, () => 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        for (var frame = 0; frame < 60; frame++)
        {
            patch.Begin(1, frame / 60d);
            var failed = new List<int>();
            for (var i = 0; i < triangles.Count; i += 3)
                if (!patch.TryProject(triangles[i], triangles[i + 1], triangles[i + 2], output, out _)) failed.Add(i / 3);
            if (frame >= 10) Assert.True(failed.Count == 0, $"Frame {frame}: failed fairy triangles {string.Join(",", failed)}; rays {patch.Queries}.");
        }
    }

    [Fact]
    public void RetainedGeometryStillAnimatesColorsAndExpiresWithItsFloorEvidence()
    {
        var terrain = new Terrain(); terrain.Quad(-2, 2, -2, 2, 0);
        var patch = new TrailGroundPatch(terrain.Cast, () => 0); patch.Begin(1, 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        Assert.True(patch.TryProject(Footprint[0], Footprint[1], Footprint[2], output, out var n));
        var original = output[..n];
        var recolored = Footprint.Select(v => v with { Color = v.Color * .25f }).ToArray();
        patch.Begin(1, .1);
        Assert.True(patch.TryProject(recolored[0], recolored[1], recolored[2], output, out n));
        Assert.Equal(0, patch.Queries);
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(original[i].Position, output[i].Position);
            Assert.InRange(Vector4.Distance(original[i].Color * .25f, output[i].Color), 0, .00001f);
        }
        terrain.Faces.Clear(); patch.Begin(1, .61);
        Assert.False(patch.TryProject(recolored[0], recolored[1], recolored[2], output, out n));
        Assert.Equal(0, n);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedMissRevokesRemovedOrRaisedSupportWithinCacheLifetime(bool raised)
    {
        var terrain = new Terrain(); terrain.Quad(-2, 2, -2, 2, 0);
        TrailGroundVertex[] source = [V(-.2f, -.2f), V(.2f, -.2f), V(-.2f, .2f)];
        var patch = new TrailGroundPatch(terrain.Cast, () => 0); patch.Begin(1, 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        Assert.True(patch.TryProject(source[0], source[1], source[2], output, out _));

        terrain.Faces.Clear();
        if (raised) terrain.Quad(-2, 2, -2, 2, .75f);
        // The proactive ray now passes through the old floor without a hit.
        // In the raised case a broad ray still finds the platform, but its
        // shorter near-layer ray proves that the original support is gone.
        patch.Begin(1, .31);
        Assert.True(patch.MissedQueries > 0);
        Assert.False(patch.TryProject(source[0], source[1], source[2], output, out var count));
        Assert.Equal(0, count);
        Assert.InRange(patch.Queries, 1, TrailGroundPatch.MaximumQueries);
        if (!raised) return;

        // A near-layer miss must not retire the platform above that ray origin.
        var upper = source.Select(v => v with { Position = v.Position + Vector3.UnitY * .75f }).ToArray();
        Assert.True(patch.TryProject(upper[0], upper[1], upper[2], output, out count));
        AssertArea(upper, output[..count]); AssertOnTerrain(terrain, output[..count]);
    }

    [Fact]
    public void LowerFreshHitRevokesTheOldFloorAboveItButKeepsTheNewSupport()
    {
        var terrain = new Terrain(); terrain.Quad(-2, 2, -2, 2, 0);
        TrailGroundVertex[] source = [V(-.2f, -.2f), V(.2f, -.2f), V(-.2f, .2f)];
        var patch = new TrailGroundPatch(terrain.Cast, () => 0); patch.Begin(1, 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        Assert.True(patch.TryProject(source[0], source[1], source[2], output, out _));

        terrain.Faces.Clear(); terrain.Quad(-2, 2, -2, 2, -.2f);
        patch.Begin(1, .31);
        Assert.True(patch.TryProject(source[0], source[1], source[2], output, out var count));
        AssertArea(source, output[..count]); AssertOnTerrain(terrain, output[..count]);
        Assert.All(output[..count], v => Assert.InRange(v.Position.Y, -.04001f, -.03999f));
        Assert.InRange(patch.Queries, 1, TrailGroundPatch.MaximumQueries);
    }

    [Fact]
    public void MissingLatticeWitnessOutsideThinFloorDoesNotRevokeMeasuredSupport()
    {
        var terrain = new Terrain(); terrain.Quad(0, .04f, 0, .04f, 0);
        TrailGroundVertex[] source = [V(.01f, .01f), V(.03f, .01f), V(.01f, .03f)];
        var patch = new TrailGroundPatch(terrain.Cast, () => 0); patch.Begin(1, 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        // The footprint is supported, but the enclosing lattice-cell center
        // (.0625, .0625) is outside this real narrow collision surface.
        Assert.True(patch.TryProject(source[0], source[1], source[2], output, out _));
        patch.Begin(1, .31);
        Assert.True(patch.MissedQueries > 0);
        Assert.True(patch.TryProject(source[0], source[1], source[2], output, out var count));
        AssertArea(source, output[..count]); AssertOnTerrain(terrain, output[..count]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MalformedRayResultsDoNotDisproveMeasuredFloor(int malformed)
    {
        var terrain = new Terrain(); terrain.Quad(-2, 2, -2, 2, 0);
        var lower = new Terrain(); lower.Quad(-2, 2, -2, 2, -.2f);
        var changed = false;
        TrailSurfaceHit? Cast(Vector3 origin, Vector3 direction, float distance)
        {
            if (!changed) return terrain.Cast(origin, direction, distance);
            var hit = lower.Cast(origin, direction, distance)!.Value;
            return hit with { Point = malformed switch
            {
                0 => new(float.NaN, -.2f, origin.Z),
                1 => hit.Point + Vector3.UnitX * .05f,
                _ => origin + direction * (distance + .2f),
            } };
        }
        TrailGroundVertex[] source = [V(-.2f, -.2f), V(.2f, -.2f), V(-.2f, .2f)];
        var patch = new TrailGroundPatch(Cast, () => 0); patch.Begin(1, 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        Assert.True(patch.TryProject(source[0], source[1], source[2], output, out _));
        changed = true; patch.Begin(1, .31);
        Assert.True(patch.TryProject(source[0], source[1], source[2], output, out var count));
        Assert.All(output[..count], v => Assert.Equal(TrailPlacement.GroundClearance, v.Position.Y));
    }

    [Fact]
    public void NewlyObservedRaisedTreadInvalidatesStillFreshLowerProjection()
    {
        var terrain = new Terrain(); terrain.Quad(-10, 10, -10, 10, 0);
        TrailGroundVertex[] source = [V(-.2f, -.2f), V(.2f, -.2f), V(-.2f, .2f)];
        var patch = new TrailGroundPatch(terrain.Cast, () => 0); patch.Begin(1, 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        Assert.True(patch.TryProject(source[0], source[1], source[2], output, out var count));
        Assert.All(output[..count], v => Assert.Equal(TrailPlacement.GroundClearance, v.Position.Y));

        terrain.Quad(0, 1, -1, 1, .3f);
        // Proactive witnesses see the new tread while the original retained
        // geometry and its lower floor are still within their .6-second TTL.
        patch.Begin(1, .41);
        Assert.True(patch.TryProject(source[0], source[1], source[2], output, out count));
        AssertArea(source, output[..count]); AssertOnTerrain(terrain, output[..count]);
        Assert.Contains(output[..count], v => v.Position.Y > .4f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedTwentyDotTrailKeepsEveryDotAndLineAcrossEvidenceRefresh(bool underDeck)
    {
        var terrain = new Terrain(); terrain.Quad(-5, 60, -4, 4, 0);
        if (underDeck) terrain.Quad(-5, 60, -4, 4, 1);
        var patch = new TrailGroundPatch(terrain.Cast, () => 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        var dots = new List<(TrailGroundVertex A, TrailGroundVertex B, TrailGroundVertex C)>();
        var lines = new List<(TrailGroundVertex A, TrailGroundVertex B, TrailGroundVertex C)>();
        for (var dot = 0; dot < 20; dot++)
        for (var sector = 0; sector < 12; sector++)
        {
            var center = new Vector2(1 + dot * 2.5f, .03f);
            var radius = .1f + dot * .012f;
            var angle = sector * MathF.Tau / 12; var next = (sector + 1) * MathF.Tau / 12;
            dots.Add((V(center.X, center.Y), V(center.X + MathF.Cos(angle) * radius, center.Y + MathF.Sin(angle) * radius),
                V(center.X + MathF.Cos(next) * radius, center.Y + MathF.Sin(next) * radius)));
        }
        for (var segment = 0; segment < 127; segment++)
        {
            var x = .8f + segment * .25f; var half = .04f + segment * .0015f;
            var a = V(x, -half); var b = V(x, half); var c = V(x + .25f, -half); var d = V(x + .25f, half);
            lines.Add((a, c, d)); lines.Add((a, d, b));
        }
        var firstComplete = -1; var minDots = int.MaxValue; var minLines = int.MaxValue;
        for (var frame = 0; frame < 180; frame++)
        {
            patch.Begin(1, frame / 60d);
            var visibleDots = 0; var visibleLines = 0;
            foreach (var triangle in dots) if (patch.TryProject(triangle.A, triangle.B, triangle.C, output, out _)) visibleDots++;
            var missingLines = new List<int>();
            for (var i = 0; i < lines.Count; i++)
            {
                var triangle = lines[i];
                if (patch.TryProject(triangle.A, triangle.B, triangle.C, output, out _)) visibleLines++;
                else missingLines.Add(i);
            }
            Assert.InRange(patch.Queries, 0, TrailGroundPatch.MaximumQueries);
            if (firstComplete < 0 && visibleDots == dots.Count && visibleLines == lines.Count) firstComplete = frame;
            if (firstComplete < 0) continue;
            minDots = Math.Min(minDots, visibleDots); minLines = Math.Min(minLines, visibleLines);
            Assert.True(visibleDots == dots.Count, $"Frame {frame}: {visibleDots}/{dots.Count} dot triangles.");
            Assert.True(visibleLines == lines.Count, $"Frame {frame}: {visibleLines}/{lines.Count} line triangles; missing {string.Join(",", missingLines)}, rays {patch.Queries}.");
        }
        Assert.InRange(firstComplete, 0, 40);
        testLog.WriteLine($"First complete frame {firstComplete}; frames{firstComplete}–179 minimum dot triangles {minDots}/{dots.Count}, line triangles {minLines}/{lines.Count}.");
    }

    [Fact]
    public void OutputAndInvalidGeometryFailClosed()
    {
        var terrain = Terrain.Grid(8, .2f, (_, _) => 0);
        var patch = new TrailGroundPatch(terrain.Cast, () => 0); patch.Begin(1, 0);
        Assert.False(patch.TryProject(Footprint[0], Footprint[1], Footprint[2], new TrailGroundVertex[3], out var n));
        Assert.Equal(0, n);
        var malformed = new TrailGroundPatch((o, _, _) => new(o - Vector3.UnitY * .4f, Vector3.UnitY, default, default, default));
        malformed.Begin(1, 0);
        Assert.False(malformed.TryProject(Footprint[0], Footprint[1], Footprint[2], new TrailGroundVertex[384], out n));
        Assert.Equal(0, n);
    }

    private static TrailGroundVertex[] Project(Terrain terrain, TrailGroundVertex[] source)
    {
        var patch = new TrailGroundPatch(terrain.Cast, () => 0); patch.Begin(1, 0);
        var output = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
        Assert.True(patch.TryProject(source[0], source[1], source[2], output, out var count), $"Projection did not complete after {patch.Queries} rays.");
        return output[..count];
    }

    private static void AssertArea(TrailGroundVertex[] source, TrailGroundVertex[] result)
    {
        static float Triangle(Vector3 a, Vector3 b, Vector3 c) => Math.Abs((b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X)) * .5f;
        var expected = Triangle(source[0].Position, source[1].Position, source[2].Position); var area = 0f;
        for (var i = 0; i < result.Length; i += 3) area += Triangle(result[i].Position, result[i + 1].Position, result[i + 2].Position);
        Assert.InRange(Math.Abs(expected - area), 0, .00002f);
    }

    private static void AssertOnTerrain(Terrain terrain, TrailGroundVertex[] mesh)
    {
        // Inspect interiors, not just endpoints: a triangle whose vertices touch
        // opposite treads can still cut through the raised tread between them.
        for (var i = 0; i < mesh.Length; i += 3)
        for (var a = 1; a < 8; a++)
        for (var b = 1; b < 8 - a; b++)
        {
            var p = (mesh[i].Position * a + mesh[i + 1].Position * b + mesh[i + 2].Position * (8 - a - b)) / 8;
            var hit = terrain.Cast(p + Vector3.UnitY * 4, -Vector3.UnitY, 8);
            Assert.True(hit.HasValue);
            Assert.InRange(p.Y - hit.Value.Point.Y, TrailPlacement.GroundClearance - .00002f, TrailPlacement.GroundClearance + .00002f);
        }
    }

    // Independent Moller-Trumbore intersections against explicit terrain triangles.
    private sealed class Terrain
    {
        internal readonly List<(Vector3 A, Vector3 B, Vector3 C)> Faces = [];
        internal TrailSurfaceHit? Cast(Vector3 origin, Vector3 direction, float length)
        {
            TrailSurfaceHit? nearest = null; var closest = length;
            foreach (var (a, b, c) in Faces)
            {
                var e1 = b - a; var e2 = c - a; var h = Vector3.Cross(direction, e2); var det = Vector3.Dot(e1, h);
                if (Math.Abs(det) < 1e-8f) continue;
                var inverse = 1 / det; var s = origin - a; var u = inverse * Vector3.Dot(s, h);
                if (u < -1e-6 || u > 1 + 1e-6) continue;
                var q = Vector3.Cross(s, e1); var v = inverse * Vector3.Dot(direction, q);
                if (v < -1e-6 || u + v > 1 + 1e-6) continue;
                var distance = inverse * Vector3.Dot(e2, q);
                if (distance < 0 || distance > closest) continue;
                closest = distance; nearest = new(origin + direction * distance, Vector3.Normalize(Vector3.Cross(e1, e2)), a, b, c);
            }
            return nearest;
        }
        internal void Quad(float left, float right, float near, float far, float height)
        {
            Vector3 a = new(left, height, near), b = new(right, height, near), c = new(left, height, far), d = new(right, height, far);
            Faces.Add((a, b, c)); Faces.Add((b, d, c));
        }
        internal static Terrain Grid(int n, float step, Func<float, float, float> height)
        {
            var terrain = new Terrain();
            Vector3 P(int x, int z) { var px = (x - n / 2f) * step; var pz = (z - n / 2f) * step; return new(px, height(px, pz), pz); }
            for (var x = 0; x < n; x++) for (var z = 0; z < n; z++)
            { terrain.Faces.Add((P(x, z), P(x + 1, z), P(x, z + 1))); terrain.Faces.Add((P(x + 1, z), P(x + 1, z + 1), P(x, z + 1))); }
            return terrain;
        }
    }
}
