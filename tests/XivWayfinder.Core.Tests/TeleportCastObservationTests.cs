using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public sealed class TeleportCastObservationTests
{
    [Fact]
    public void ActualTeleportUsesObservedTotalCastTimeAndCannotGoNegative()
    {
        Assert.True(TeleportCastObservation.TryRemaining(true,1,5,1.25f,5,out var remaining));
        Assert.Equal(3.75f, remaining);
        Assert.True(TeleportCastObservation.TryRemaining(true,1,5,5.1f,5,out remaining));
        Assert.Equal(0, remaining);
    }

    [Theory]
    [InlineData(false,1,5)]
    [InlineData(true,1,6)]
    [InlineData(true,5,7)]
    [InlineData(true,5,5)]
    [InlineData(true,1,0)]
    [InlineData(true,0,5)]
    public void MenuReturnUnrelatedCastOrStoppedCastNeverStartsJoining(bool casting, byte type, uint id)
    {
        Assert.False(TeleportCastObservation.TryRemaining(casting,type,id,1,5,out var remaining));
        Assert.Equal(0,remaining);
    }

    [Theory]
    [InlineData(float.NaN,5)]
    [InlineData(0,float.NaN)]
    [InlineData(float.PositiveInfinity,5)]
    [InlineData(0,float.PositiveInfinity)]
    [InlineData(-1,5)]
    [InlineData(0,0)]
    public void MissingOrMalformedCastTimingIsNotAReadyBoardingCue(float current, float total)
    {
        Assert.False(TeleportCastObservation.TryRemaining(true,1,5,current,total,out var remaining));
        Assert.Equal(0,remaining);
    }
}
