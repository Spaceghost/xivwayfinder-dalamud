using System.Numerics;
using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionTeleportIntentTests
{
    private static CompanionTeleportInput Input(Vector3 player = default, Vector3? guide = null) =>
        new(132, 8, player, 0, guide, true, true);

    [Fact]
    public void InitialStopCueRequestsANearbyPointButDoesNotPlaceTheGuide()
    {
        var intent = new CompanionTeleportIntent();
        var frame = intent.Update(Input(), 0);
        Assert.Equal(CompanionTeleportMode.RequestStop, frame.Mode);
        Assert.Equal(new Vector3(.35f, 0, 1.65f), frame.Rendezvous);
        Assert.False(frame.RequestRest); Assert.False(frame.RequestBeckon); Assert.False(frame.ReadyToJoin);
    }

    [Fact]
    public void TurningInPlaceDoesNotOrbitOrRetargetTheCompanion()
    {
        var intent = new CompanionTeleportIntent();
        var first = intent.Update(Input(), 0);
        for (var i = 1; i <= 600; i++)
        {
            var frame = intent.Update(Input() with { PlayerYaw = i * .04f }, i / 60d);
            Assert.Equal(first.Rendezvous, frame.Rendezvous); Assert.Equal(first.Revision, frame.Revision);
            Assert.Equal(CompanionTeleportMode.RequestStop, frame.Mode);
        }
    }

    [Fact]
    public void OnlyValidatedArrivalAllowsRestOrBeckoningRequest()
    {
        var intent = new CompanionTeleportIntent();
        var target = intent.Update(Input(), 0).Rendezvous;
        var frame = intent.Update(Input(guide: target), .1);
        Assert.True(frame.RequestBeckon); Assert.False(frame.RequestRest);
        for (var i = 2; i <= 30; i++) frame = intent.Update(Input(guide: target), i * .1);
        Assert.True(frame.RequestRest); Assert.False(frame.RequestBeckon);
        frame = intent.Update(Input(guide: new Vector3(20, 0, 20)), 3.1);
        Assert.False(frame.RequestRest); Assert.False(frame.RequestBeckon);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void RunningGetsDiscreteSheepdogRequestsNotACharacterCoordinateAttachment(int fps)
    {
        var intent = new CompanionTeleportIntent();
        var frame = intent.Update(Input(), 0);
        var revisions = new HashSet<long> { frame.Revision };
        var left = false; var right = false; var heldFrames = 0;
        for (var i = 1; i <= fps * 8; i++)
        {
            var before = frame;
            frame = intent.Update(Input(new(0, 0, i / (float)fps * 3)), i / (double)fps);
            if (frame.Revision == before.Revision)
            { Assert.Equal(before.Rendezvous, frame.Rendezvous); heldFrames++; }
            else revisions.Add(frame.Revision);
            if (frame.Mode == CompanionTeleportMode.Corral)
            { left |= frame.Rendezvous!.Value.X < 0; right |= frame.Rendezvous!.Value.X > 0; }
        }
        Assert.Equal(CompanionTeleportMode.Corral, frame.Mode);
        Assert.True(left && right); Assert.InRange(revisions.Count, 5, 13);
        Assert.True(heldFrames > fps * 6);
    }

    [Fact]
    public void SmallPositionNoiseDoesNotChurnRoutes()
    {
        var intent = new CompanionTeleportIntent();
        var first = intent.Update(Input(), 0);
        for (var i = 1; i < 300; i++)
        {
            var frame = intent.Update(Input(new(MathF.Sin(i) * .001f, 0, 0)), i / 60d);
            Assert.Equal(first.Revision, frame.Revision);
        }
    }

    [Fact]
    public void RealTeleportCastAsksForApproachAndDoesNotTeleportADistantGuide()
    {
        var intent = new CompanionTeleportIntent();
        intent.Update(Input(guide: new(10, 0, 10)), 0);
        var frame = intent.Update(Input(guide: new(10, 0, 10)) with { TeleportCasting = true, CastRemaining = 4 }, .1);
        Assert.Equal(CompanionTeleportMode.JoinCast, frame.Mode);
        Assert.Equal(new Vector3(.45f, 0, .1f), frame.Rendezvous); Assert.False(frame.ReadyToJoin);
        frame = intent.Update(Input(guide: frame.Rendezvous) with { TeleportCasting = true, CastRemaining = 3 }, .2);
        Assert.True(frame.ReadyToJoin); Assert.False(frame.RequestRest); Assert.False(frame.RequestBeckon);
        frame = intent.Update(Input(guide: frame.Rendezvous) with { TeleportCasting = true, CastRemaining = .1f }, .3);
        Assert.Equal(CompanionTeleportMode.AwaitDeparture, frame.Mode);
    }

    [Fact]
    public void InterruptedCastReturnsToStopCueRatherThanInventingDeparture()
    {
        var intent = new CompanionTeleportIntent();
        var casting = intent.Update(Input() with { TeleportCasting = true, CastRemaining = 2 }, 0);
        var stopped = intent.Update(Input(), .1);
        Assert.Equal(CompanionTeleportMode.RequestStop, stopped.Mode);
        Assert.True(stopped.Revision > casting.Revision); Assert.False(stopped.ReadyToJoin);
    }

    [Fact]
    public void OtherCastsAreNotTeleportBoardingSignals()
    {
        var intent = new CompanionTeleportIntent();
        var frame = intent.Update(Input() with { TeleportCasting = false, CastRemaining = 2 }, 0);
        Assert.Equal(CompanionTeleportMode.RequestStop, frame.Mode); Assert.False(frame.ReadyToJoin);
    }

    [Fact]
    public void MissingVisibilityAndZoneChangesInvalidateOldRequestIdentities()
    {
        var intent = new CompanionTeleportIntent();
        var old = intent.Update(Input(), 0);
        var hidden = intent.Update(Input() with { Visible = false }, .1);
        Assert.False(hidden.Active); Assert.True(hidden.Revision > old.Revision);
        var returned = intent.Update(Input(new(50, 4, 50)) with { Territory = 133 }, .2);
        Assert.True(returned.Active); Assert.True(returned.Revision > hidden.Revision);
        Assert.Equal(4, returned.Rendezvous!.Value.Y); Assert.InRange(returned.Rendezvous.Value.X, 50, 51);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidClocksDoNotAdvanceOrRetarget(double clock)
    {
        var intent = new CompanionTeleportIntent(); var old = intent.Update(Input(), 0);
        Assert.Equal(old, intent.Update(Input(new(1, 0, 0)), clock));
    }

    [Fact]
    public void LongPauseOrClockReversalDoesNotCountAsRunningAway()
    {
        var intent = new CompanionTeleportIntent(); intent.Update(Input(), 0);
        var paused = intent.Update(Input(new(2, 0, 0)), 20);
        Assert.Equal(CompanionTeleportMode.RequestStop, paused.Mode);
        var reversed = intent.Update(Input(new(2, 0, 0)), 10);
        Assert.Equal(CompanionTeleportMode.RequestStop, reversed.Mode);
        Assert.Equal(paused.Revision, reversed.Revision);
    }

    [Fact]
    public void InvalidWorldStateClearsRatherThanProducingMalformedQueries()
    {
        var intent = new CompanionTeleportIntent(); intent.Update(Input(), 0);
        Assert.False(intent.Update(Input(new(float.NaN)), .1).Active);
        Assert.False(intent.Update(Input() with { Territory = 0 }, .2).Active);
        Assert.False(intent.Update(Input() with { PlayerYaw = float.PositiveInfinity }, .3).Active);
        Assert.False(intent.Update(Input() with { TeleportNeeded = false }, .4).Active);
    }
}
