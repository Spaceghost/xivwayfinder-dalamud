using System.Numerics;
using Xunit;
namespace XivWayfinder.Core.Tests;

public class ZoneRouteTests
{
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
