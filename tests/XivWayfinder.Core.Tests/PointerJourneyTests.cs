using System.Numerics;
namespace XivWayfinder.Core.Tests;
public sealed class PointerJourneyTests
{
    private readonly Target target = new(TargetSource.Explicit, 1, 0, 15, 0, "Destination");
    private static readonly MinionPose Companion = new(new(0, 2.4f, 1.6f), 0, 0);
    [Fact]
    public void OnePoseRushesToDestinationAndReturnsWhenSightIsLost()
    {
        var journey = new PointerJourney();
        var a = journey.Update(Companion, target, false, false, Vector3.Zero, 0, .1f, false);
        var b = journey.Update(Companion, target, true, false, Vector3.Zero, .1, .1f, false);
        Assert.Equal(Companion, a.Pose); Assert.True(b.AtDestination);
        Assert.InRange(b.Pose!.Value.Position.Z, 1.7f, 5); Assert.InRange(b.Pose.Value.Pitch, -1.571f, -1.57f);
        for (var i = 0; i < 40; i++) b = journey.Update(Companion, target, true, false, Vector3.Zero, 1, .1f, false);
        Assert.InRange(b.Pose!.Value.Position.Z, 14.99f, 15.01f);
        var c = journey.Update(Companion, target, false, false, Vector3.Zero, 1, .1f, false);
        Assert.False(c.AtDestination);Assert.True(c.Pose!.Value.Position.Z < b.Pose.Value.Position.Z);Assert.Equal(0, c.Hum);
    }
    [Fact]
    public void HumGrowsWithApproachAndArrivalRingsOnlyOnce()
    {
        var journey = new PointerJourney();
        var far = journey.Update(Companion, target, true, false, Vector3.Zero, 0, .1f, true);
        var near = journey.Update(Companion, target, true, false, new(0, 0, 14), 1, .1f, true);
        Assert.True(near.Hum > far.Hum); Assert.False(near.Chime);
        // At the player's feet the destination can leave the camera frustum.
        Assert.True(journey.Update(Companion, target, false, true, new(0, 0, 15), 2, .1f, true).Chime);
        Assert.False(journey.Update(Companion, target, true, true, new(0, 0, 15), 3, .1f, true).Chime);
        journey.Hide();
        Assert.False(journey.Update(Companion, target, true, true, new(0, 0, 15), 4, .1f, true).Chime);
    }
    [Fact]
    public void DoesNotRingWhenLoadingAtAnAlreadyReachedDestination()
    {
        var journey = new PointerJourney();
        Assert.False(journey.Update(Companion, target, true, true, new(0, 0, 15), 0, .1f, false).Chime);
        Assert.Equal(default, journey.Update(null, target, true, false, Vector3.Zero, 1, .1f, false));
    }
    [Fact]
    public void ANewDestinationCanChimeAfterThePreviousOne()
    {
        var journey = new PointerJourney();
        journey.Update(Companion, target, true, false, Vector3.Zero, 0, .1f, true);
        Assert.True(journey.Update(Companion, target, true, true, Vector3.Zero, 1, .1f, true).Chime);
        var next = target with { Z = 18 };
        journey.Update(Companion, next, true, false, Vector3.Zero, 2, .1f, true);
        Assert.True(journey.Update(Companion, next, true, true, Vector3.Zero, 3, .1f, true).Chime);
    }
    [Fact]
    public void ReducedMotionPlacesExactlyOverTheDestination()
    {
        var frame = new PointerJourney().Update(Companion, target, true, false, Vector3.Zero, 0, .1f, true);
        Assert.Equal(new Vector3(0, 2.4f, 15), frame.Pose!.Value.Position);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisibleDistantDestinationKeepsTheCursorWithThePlayer(bool reducedMotion)
    {
        var journey = new PointerJourney();
        var far = target with { Z = 232 };
        for (var i = 0; i < 600; i++)
        {
            var frame = journey.Update(Companion, far, true, false, Vector3.Zero, i / 60.0, 1f / 60, reducedMotion);
            Assert.Equal(Companion, frame.Pose);
            Assert.False(frame.AtDestination);
            Assert.Equal(0, frame.Hum);
            Assert.False(frame.Chime);
        }
    }
    [Fact]
    public void DestinationRangeUsesHeightAsWellAsHorizontalDistance()
    {
        var frame = new PointerJourney().Update(Companion, target with { Y = 100 }, true, false,
            Vector3.Zero, 0, .1f, true);
        Assert.Equal(Companion, frame.Pose);
        Assert.False(frame.AtDestination);
    }
    [Fact]
    public void NearbyApproachHasHysteresisAndReturnsWhenThePlayerWalksAway()
    {
        var journey = new PointerJourney();
        var goal = target with { Z = 50 };
        PointerJourneyFrame At(float distance) => journey.Update(Companion, goal, true, false,
            new(0, 0, 50 - distance), 0, .1f, true);
        Assert.False(At(21).AtDestination);
        Assert.True(At(20).AtDestination);
        Assert.True(At(23).AtDestination);
        Assert.False(At(25).AtDestination);
        Assert.Equal(Companion, At(25).Pose);
        Assert.False(At(21).AtDestination);
        Assert.True(At(19).AtDestination);
        // Neither a new target nor a hide/show may inherit the release band.
        Assert.False(journey.Update(Companion, goal with { Z = 52 }, true, false,
            new(0, 0, 31), 0, .1f, true).AtDestination);
        Assert.True(At(19).AtDestination);
        journey.Hide();
        Assert.False(At(21).AtDestination);
    }
    [Fact]
    public void SynthIsQuietBoundedAndBellDecays()
    {
        long early = 0, late = 0; var peak = 0;
        for (var i = 0; i < 24000; i++)
        {
            var t = (double)i / 24000;
            var a = PointerSound.Sample(t, 1, t, 1); var b = PointerSound.Sample(t, 0, t + 5, 1);
            early += Math.Abs(a); late += Math.Abs(b); peak = Math.Max(peak, Math.Abs(a));
            Assert.Equal(0, PointerSound.Sample(t, 1, t, 0));
        }
        Assert.True(early > late * 10); Assert.InRange(peak, 1000, 16000);
        Assert.Equal(0, PointerSound.Sample(double.NaN, 1, 0, 1));
    }
}
