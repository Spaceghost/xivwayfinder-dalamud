using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class TrailSurfaceTests
{
    private static TrailSurfaceHit Floor(Vector3 point) => new(point, Vector3.UnitY, default, default, default);

    [Fact]
    public void NearbySamplesOnDifferentStepsKeepTheirOwnGroundHeight()
    {
        var sampler = new TrailSurfaceSampler((origin, _, _) => Floor(new(origin.X, origin.X == 0 ? 0 : 0.5f, origin.Z)));
        sampler.Begin(1);
        Assert.True(sampler.TryPlace(Vector3.Zero, 0, out var lower));
        Assert.True(sampler.TryPlace(new(0.01f, 0, 0), 0, out var upper));
        Assert.Equal(TrailPlacement.GroundClearance, lower.Y);
        Assert.Equal(0.5f + TrailPlacement.GroundClearance, upper.Y);
        Assert.Equal(0.01f, upper.X);
    }

    [Fact]
    public void CachedGroundRetainsTheMeasuredNormalForItsEntireMark()
    {
        var normal = Vector3.Normalize(new Vector3(-1, 1, 0));
        var calls = 0;
        var sampler = new TrailSurfaceSampler((_, _, _) =>
        {
            calls++;
            return new(Vector3.Zero, normal, default, default, default);
        });
        sampler.Begin(1);
        Assert.True(sampler.TryPlace(Vector3.Zero, 0, out var at, out var measured));
        Assert.Equal(TrailPlacement.GroundClearance, at.Y);
        Assert.True(Vector3.Distance(normal, measured) < .00001f);
        Assert.True(sampler.TryPlace(Vector3.Zero, .2, out _, out var cached));
        Assert.Equal(measured, cached);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void TerrainBetweenVerifiedEndpointsBreaksTheLine()
    {
        var sampler = new TrailSurfaceSampler((origin, direction, _) => direction.Y == -1
            ? Floor(new(origin.X, 0, origin.Z))
            : Floor(new(0.125f, 0.2f, 0))); // A ridge rises between the two floor samples.
        sampler.Begin(1);
        Assert.True(sampler.TryPlace(Vector3.Zero, 0, out var a));
        Assert.True(sampler.TryPlace(new(0.25f, 0, 0), 0, out var b));
        Assert.False(sampler.CanConnect(a, b, 0));
    }

    [Fact]
    public void OverheadSegmentsAlsoStopAtTallerRidgesBetweenSamples()
    {
        var sampler = new TrailSurfaceSampler((origin, direction, _) => direction.Y == -1
            ? Floor(new(origin.X, 0, origin.Z))
            : Floor(new(0.5f, 3.16f, 0)));
        sampler.Begin(1);
        Assert.True(sampler.TryPlaceOverhead(Vector3.Zero, Vector3.Zero, 3, 0, out var a));
        Assert.True(sampler.TryPlaceOverhead(Vector3.UnitX, Vector3.Zero, 3, 0, out var b));
        Assert.False(sampler.CanConnect(a, b, 0));
    }

    [Fact]
    public void AClearSlopingSurfaceCanConnectWithoutAnArbitraryHeightDifferenceCutoff()
    {
        Vector3 castOrigin = default, castDirection = default;
        float castDistance = 0;
        var sampler = new TrailSurfaceSampler((origin, direction, distance) =>
        {
            castOrigin = origin; castDirection = direction; castDistance = distance;
            return null;
        });
        sampler.Begin(1);
        var a = new Vector3(0, 0.16f, 0);
        var b = new Vector3(0.25f, 0.36f, 0);
        Assert.True(sampler.CanConnect(a, b, 0));
        Assert.Equal(a, castOrigin);
        Assert.True(Vector3.Distance(b, castOrigin + castDirection * castDistance) < 0.0001f);
    }

    [Fact]
    public void MissingOrAmbiguousGroundIsNeverReplacedWithTheRouteHeight()
    {
        TrailSurfaceHit? hit = null;
        var sampler = new TrailSurfaceSampler((_, _, _) => hit);
        sampler.Begin(1);
        Assert.False(sampler.TryPlace(Vector3.Zero, 0, out _));
        Assert.False(sampler.TryPlaceOverhead(Vector3.Zero, Vector3.Zero, 3, 0, out _));

        hit = Floor(new(0, -5, 0)); // A lower storey is outside the route's floor tolerance.
        Assert.False(sampler.TryPlace(Vector3.Zero, 1, out _));
        hit = new(Vector3.Zero, Vector3.UnitX, default, default, default);
        Assert.False(sampler.TryPlace(Vector3.Zero, 2, out _));
    }

    [Fact]
    public void OverheadUsesTheCorrectedFloorAndStaysAboveThePlayer()
    {
        var sampler = new TrailSurfaceSampler((origin, _, _) => Floor(new(origin.X, 1, origin.Z)));
        sampler.Begin(1);
        Assert.True(sampler.TryPlaceOverhead(Vector3.Zero, Vector3.Zero, 3, 0, out var aboveFloor));
        Assert.Equal(1 + TrailPlacement.GroundClearance + 3, aboveFloor.Y);
        Assert.True(sampler.TryPlaceOverhead(Vector3.Zero, new(0, 10, 0), -10, 0, out var abovePlayer));
        Assert.Equal(12.5f, abovePlayer.Y);
    }

    [Fact]
    public void ExhaustedBudgetCreatesGapsAndDoesNotReuseAnExpiredFloor()
    {
        var calls = 0;
        var height = 0f;
        var sampler = new TrailSurfaceSampler((origin, _, _) => { calls++; return Floor(new(origin.X, height, origin.Z)); });
        sampler.Begin(1);
        Assert.True(sampler.TryPlace(Vector3.Zero, 0, out _));
        for (var i = 1; i < 4; i++) Assert.True(sampler.TryPlace(new(i, 0, 0), 0, out _));
        Assert.False(sampler.TryPlace(new(4, 0, 0), 0, out _));
        Assert.Equal(4, calls);
        Assert.True(sampler.TryPlace(Vector3.Zero, 0.1, out _)); // Exact, fresh samples cost no additional query.
        height = 1;
        Assert.False(sampler.TryPlace(Vector3.Zero, 3, out _)); // Stale samples are not shown when refresh is unavailable.
        sampler.Begin(1);
        Assert.True(sampler.TryPlace(Vector3.Zero, 3, out var refreshed));
        Assert.Equal(1 + TrailPlacement.GroundClearance, refreshed.Y);
    }

    [Fact]
    public void FloorAndLineBudgetsAreIndependentAndResetNextFrame()
    {
        var floorCalls = 0;
        var segmentCalls = 0;
        var sampler = new TrailSurfaceSampler((origin, direction, _) =>
        {
            if (direction.Y == -1) { floorCalls++; return Floor(new(origin.X, 0, origin.Z)); }
            segmentCalls++; return null;
        });
        sampler.Begin(1);
        for (var i = 0; i < 4; i++) Assert.True(sampler.TryPlace(new(i, 0, 0), 0, out _));
        for (var i = 0; i < 4; i++) Assert.True(sampler.CanConnect(new(i, 0.16f, 0), new(i + 0.25f, 0.16f, 0), 0));
        Assert.False(sampler.CanConnect(new(4, 0.16f, 0), new(4.25f, 0.16f, 0), 0));
        Assert.Equal(4, floorCalls);
        Assert.Equal(4, segmentCalls);
        sampler.Begin(1);
        Assert.True(sampler.CanConnect(new(4, 0.16f, 0), new(4.25f, 0.16f, 0), 0));
    }

    [Fact]
    public void TerritoryChangeAndTimeResetDiscardOldAnswers()
    {
        var height = 0f;
        var calls = 0;
        var sampler = new TrailSurfaceSampler((origin, _, _) => { calls++; return Floor(new(origin.X, height, origin.Z)); });
        sampler.Begin(1);
        Assert.True(sampler.TryPlace(Vector3.Zero, 5, out _));
        height = 0.5f;
        sampler.Begin(2);
        Assert.True(sampler.TryPlace(Vector3.Zero, 5, out var changedZone));
        Assert.Equal(0.5f + TrailPlacement.GroundClearance, changedZone.Y);
        height = 1;
        Assert.True(sampler.TryPlace(Vector3.Zero, 0, out var resetClock));
        Assert.Equal(1 + TrailPlacement.GroundClearance, resetClock.Y);
        Assert.Equal(5, calls); // Elevated recovery hits each require the nearby ray plus the wider ray.
    }

    [Fact]
    public void NonFinitePositionsAndDegenerateSurfacesNeverReachTheNativeRaycaster()
    {
        var calls = 0;
        var sampler = new TrailSurfaceSampler((_, _, _) => { calls++; return null; });
        sampler.Begin(1);
        Assert.False(sampler.TryPlace(new(float.NaN, 0, 0), 0, out _));
        Assert.False(sampler.TryPlace(Vector3.Zero, double.NaN, out _));
        Assert.False(sampler.TryPlaceOverhead(Vector3.Zero, new(0, float.PositiveInfinity, 0), 3, 0, out _));
        Assert.False(sampler.CanConnect(Vector3.Zero, new(float.NaN, 0, 0), 0));
        Assert.False(sampler.CanConnect(Vector3.Zero, Vector3.Zero, 0));
        Assert.Equal(0, calls);
        Assert.False(TrailPlacement.TryGround(Vector3.Zero, new(Vector3.Zero, Vector3.Zero, default, default, default), out _));
        Assert.False(TrailPlacement.TryGround(Vector3.Zero, Floor(new(0.1f, 0, 0)), out _));
    }

    [Fact]
    public void GroundTrailRemainsCompleteWhileRunningAlongAnAnchoredRoute()
    {
        var queriesThisFrame = 0;
        var sampler = new TrailSurfaceSampler((origin, direction, _) =>
        {
            queriesThisFrame++;
            return direction.Y == -1 ? Floor(new(origin.X, origin.X * 0.1f, origin.Z)) : null;
        });
        Vector3[] route = [Vector3.Zero, new(300, 30, 0)];
        var beads = new TrailPoint[48];
        var line = new TrailPoint[128];
        // Six yalms/second, 60fps, for four seconds: includes two-second cache expiration while moving.
        for (var frame = 0; frame < 240; frame++)
        {
            var player = new Vector3(frame * 0.1f, frame * 0.01f, 0);
            var arc = PathTrail.Project(route, player, out _);
            var now = frame / 60d;
            sampler.Begin(1);
            queriesThisFrame = 0;
            var n = PathTrail.Sample(route, arc, 2.5f, 0.8f, 27, beads);
            for (var i = 0; i < n; i++) sampler.TryPlace(beads[i].Position, now, out _);
            var m = PathTrail.Sample(route, arc, 0.25f, 0.8f, 27, line);
            var connected = 0;
            var previousOk = false;
            var previous = Vector3.Zero;
            for (var i = 0; i < m; i++)
            {
                var ok = sampler.TryPlace(line[i].Position, now, out var at);
                if (ok && previousOk && sampler.CanConnect(previous, at, now)) connected++;
                previous = at;
                previousOk = ok;
            }
            Assert.InRange(queriesThisFrame, 0, 8);
            if (frame >= 60) Assert.True(connected >= (m - 1) * 0.95f, $"Frame {frame}: only {connected}/{m - 1} line segments available while running.");
        }
    }

    [Fact]
    public void DescendingOverheadTrailChecksItsActualChangingHeightWithinTheTimeBudget()
    {
        var queryTime = 0d;
        var segmentQueries = 0;
        var sampler = new TrailSurfaceSampler((origin, direction, _) =>
        {
            if (direction.Y == -1) return Floor(new(origin.X, -origin.X * 0.1f, origin.Z));
            queryTime += 0.000005; // A five-microsecond collision query, measured by the injected clock.
            segmentQueries++;
            return null;
        }, () => queryTime);
        Vector3[] route = [Vector3.Zero, new(200, -20, 0)];
        var line = new TrailPoint[128];
        for (var frame = 0; frame < 180; frame++)
        {
            var player = new Vector3(frame * 0.1f, -frame * 0.01f, 0);
            var arc = PathTrail.Project(route, player, out _);
            var count = PathTrail.Sample(route, arc, 1, 0.8f, 27, line);
            var now = frame / 60d;
            sampler.Begin(1, true);
            segmentQueries = 0;
            var connected = 0;
            var previousOk = false;
            var previous = Vector3.Zero;
            for (var i = 0; i < count; i++)
            {
                var ok = sampler.TryPlaceOverhead(line[i].Position, player, 3, now, out var at);
                if (ok) Assert.True(at.Y >= player.Y + 3);
                if (ok && previousOk && sampler.CanConnect(previous, at, now)) connected++;
                previous = at;
                previousOk = ok;
            }
            if (frame >= 30)
            {
                Assert.Equal(count - 1, connected);
                Assert.Equal(count - 1, segmentQueries); // No prior frame's different height was reused.
            }
        }
    }

    [Fact]
    public void SlowCollisionQueriesCannotTurnTheAdaptivePassIntoAnUnboundedBurst()
    {
        var queryTime = 0d;
        var queries = 0;
        var sampler = new TrailSurfaceSampler((_, _, _) => { queryTime += 0.001; queries++; return null; }, () => queryTime);
        sampler.Begin(1, true);
        for (var i = 0; i < 4; i++) Assert.True(sampler.CanConnect(new(i, 3, 0), new(i + 1, 3, 0), 0));
        Assert.False(sampler.CanConnect(new(4, 3, 0), new(5, 3, 0), 0));
        Assert.Equal(4, queries);
        Assert.True(sampler.CanConnect(new(0, 3, 0), new(1, 3, 0), 0)); // A verified exact segment remains usable.
    }

    [Fact]
    public void AdaptiveSegmentChurnHasACountLimitAndCannotEvictStableFloorSamples()
    {
        var floorQueries = 0;
        var sampler = new TrailSurfaceSampler((origin, direction, _) =>
        {
            if (direction.Y != -1) return null;
            floorQueries++;
            return Floor(new(origin.X, 0, origin.Z));
        }, () => 0); // Even a clock reporting free queries must have a hard bound.
        sampler.Begin(1, true);
        Assert.True(sampler.TryPlace(Vector3.Zero, 0, out _));
        for (var frame = 0; frame < 66; frame++)
        {
            sampler.Begin(1, true);
            for (var i = 0; i < 128; i++)
            {
                var x = frame * 129 + i;
                Assert.True(sampler.CanConnect(new(x, 3, 0), new(x + 1, 3, 0), frame / 60d));
            }
            var unavailable = frame * 129 + 128;
            Assert.False(sampler.CanConnect(new(unavailable, 3, 0), new(unavailable + 1, 3, 0), frame / 60d));
        }
        Assert.True(sampler.TryPlace(Vector3.Zero, 1.1, out _));
        Assert.Equal(1, floorQueries);
    }
}
