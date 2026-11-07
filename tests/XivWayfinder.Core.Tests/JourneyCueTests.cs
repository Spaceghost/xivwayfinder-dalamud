using System.Numerics;
using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public class JourneyCueTests
{
    private static readonly Target Target = new(TargetSource.Quest, 10, 1, 2, null, "Meet the guide");

    [Theory]
    [InlineData(10f, "above")]
    [InlineData(-10f, "below")]
    public void DifferentFloorIsExplainedWithOrWithoutAWalkablePath(float height, string direction)
    {
        var guide = TargetPicker.Plan(10, Vector3.Zero, Target with { Y = height }, 7, []);
        Assert.Contains(direction, JourneyCue.From(guide, "Zone", true, "").Next);
        var unavailable = JourneyCue.From(guide, "Zone", false, "waiting for mesh");
        Assert.Contains(direction, unavailable.Next);
        Assert.Contains("waiting for mesh", unavailable.Next);
        Assert.False(unavailable.HasWalkablePath);
    }

    [Fact]
    public void TeleportExplainsTheFollowingWalkingLeg()
    {
        var guide = new Guide(GuideKind.Teleport, Target, 0, Vector2.Zero, new(1, "Town", 10, 0, 0, true));
        var cue = JourneyCue.From(guide, "Zone", false, "");
        Assert.Equal("Teleport to Town", cue.Action);
        Assert.Contains("Meet the guide", cue.Next);
        Assert.False(cue.HasWalkablePath);
    }

    [Fact]
    public void LockedAetheryteIsNeverAnInstructionToTeleport()
    {
        var guide = new Guide(GuideKind.Teleport, Target, 0, Vector2.Zero, new(1, "Town", 10, 0, 0, false));
        var cue = JourneyCue.From(guide, "Zone", false, "");
        Assert.Equal("Travel to Zone", cue.Action);
        Assert.Contains("not attuned", cue.Next);
    }

    [Fact]
    public void MissingRouteAndArrivalNeverClaimTaskCompletion()
    {
        var walk = new Guide(GuideKind.Walk, Target, 20, Vector2.UnitX, null);
        Assert.Contains("waiting for mesh", JourneyCue.From(walk, "Zone", false, "waiting for mesh").Next);
        Assert.True(JourneyCue.From(walk, "Zone", true, "").HasWalkablePath);
        Assert.Contains("arrival does not complete", JourneyCue.From(walk with { Kind = GuideKind.Arrived }, "Zone", false, "").Next);
    }
}
