using System.Numerics;
using Xunit;
namespace XivWayfinder.Core.Tests;

public class ZoneRouteTests
{
    [Fact]
    public void ExitVolumeHeightDoesNotBecomeTheWalkingFloor()
    {
        // Middle La Noscea -> Limsa exit data: the trigger center is high above
        // the navmesh, so pathfinding to the literal center finds no polygon.
        var exit = new ZoneConnection(134, 129, new(-59.0843f, 83.72464f, 161.7435f), "Enter Limsa");
        var final = new Target(TargetSource.Quest, 129, 20, 30, 40, "It's Probably Pirates", true);
        var leg = exit.WalkingTarget(final);
        Assert.Null(leg.Y);
        Assert.Equal(new Vector3(-59.0843f, 32.6f, 161.7435f), leg.Position(32.6f));
        Assert.Equal(134u, leg.TerritoryId);
        Assert.Equal(final.Source, leg.Source);
        Assert.Equal(exit.Action, leg.Label);
        Assert.True(leg.MainScenario);
        Assert.Equal(40f, final.Y);
        Assert.Equal(129u, final.TerritoryId);
    }

    [Fact]
    public void MistJourneyAdvancesAcrossActualZonesAndDoesNotLoop()
    {
        ZoneConnection[] edges = [new(138, 134, new(10, 0, 0), "Middle"), new(134, 138, new(0, 0, 0), "Western"), new(134, 135, new(20, 0, 0), "Lower"), new(135, 339, new(30, 0, 0), "Mist")];
        Assert.Equal(134u, ZoneRoute.Next(138, 339, Vector3.Zero, edges)!.To);
        Assert.Equal(135u, ZoneRoute.Next(134, 339, Vector3.Zero, edges)!.To);
        Assert.Equal(339u, ZoneRoute.Next(135, 339, Vector3.Zero, edges)!.To);
        Assert.Null(ZoneRoute.Next(339, 339, Vector3.Zero, edges));
        Assert.Null(ZoneRoute.Next(138, 999, Vector3.Zero, edges));
    }
}
