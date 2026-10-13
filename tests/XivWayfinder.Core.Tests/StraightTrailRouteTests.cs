using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class StraightTrailRouteTests
{
    private static readonly Target Destination = new(TargetSource.Explicit, 1, 200, 0, null, "Marker");

    [Fact]
    public void WalkingAlongAnExplicitStraightLineDoesNotMoveItsSamples()
    {
        var route = new StraightTrailRoute();
        var first = route.Update(Vector3.Zero, Destination);
        var before = new TrailPoint[40];
        var count = PathTrail.Sample(first, 0, 2.5f, 0.8f, 27, before);
        for (var frame = 1; frame < 120; frame++)
        {
            var player = new Vector3(frame * 0.1f, 0, 0);
            var path = route.Update(player, Destination);
            Assert.Same(first, path);
            var points = new TrailPoint[40];
            var n = PathTrail.Sample(path, PathTrail.Project(path, player, out _), 2.5f, 0.8f, 27, points);
            foreach (var point in points.Take(n).Where(p => p.Position.X <= before[count - 1].Position.X))
                Assert.Contains(before.Take(count), previous => previous.Position == point.Position);
        }
    }

    [Fact]
    public void NewDestinationDepartureOrFloorReanchorsTheStraightLine()
    {
        var route = new StraightTrailRoute();
        var first = route.Update(Vector3.Zero, Destination);
        var departed = route.Update(new(3, 0, 2), Destination);
        Assert.NotSame(first, departed);
        Assert.Equal(new Vector3(3, 0, 2), departed[0]);
        var movedTarget = route.Update(new(3, 0, 2), Destination with { X = 210 });
        Assert.NotSame(departed, movedTarget);
        Assert.Equal(210, movedTarget[1].X);
        var differentFloor = route.Update(new(3, 4, 2), Destination with { X = 210 });
        Assert.NotSame(movedTarget, differentFloor);
        Assert.Equal(4, differentFloor[0].Y);
        Assert.Equal(4, differentFloor[1].Y);
    }

    [Fact]
    public void ANewTerritoryCannotReuseThePreviousLine()
    {
        var route = new StraightTrailRoute();
        var first = route.Update(Vector3.Zero, Destination);
        Assert.NotSame(first, route.Update(Vector3.Zero, Destination with { TerritoryId = 2 }));
        Assert.Empty(route.Update(new(float.NaN, 0, 0), Destination));
    }
}
