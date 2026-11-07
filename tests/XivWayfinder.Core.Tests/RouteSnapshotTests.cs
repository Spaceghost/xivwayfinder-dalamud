using System.Numerics;
using System.Text.Json;
using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public class RouteSnapshotTests
{
    [Fact]
    public void OnlyValidVisibleSameZoneWalkingPathsArePublished()
    {
        var target = new Target(TargetSource.Quest, 10, 1, 2, 3, "Task");
        var state = new WayfinderState { Enabled = true, Visible = true, Navmesh = true, PlayerTerritoryId = 10,
            Guide = new(GuideKind.Walk, target, 10, Vector2.UnitX, null) };
        foreach (var s in new[] { state, state with { Visible = false }, state with { PlayerTerritoryId = 11 }, state with { Navmesh = false } })
        {
            using var doc = JsonDocument.Parse(RouteSnapshot.Encode(s, [Vector3.Zero, Vector3.One], 1000));
            Assert.Equal(s == state ? 2 : 0, doc.RootElement.GetProperty("points").GetArrayLength());
        }
        using var invalid = JsonDocument.Parse(RouteSnapshot.Encode(state, [Vector3.Zero, new(float.NaN)], 1000));
        Assert.False(invalid.RootElement.GetProperty("walkable").GetBoolean());
    }
}
