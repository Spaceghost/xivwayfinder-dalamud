using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionCruiseSpeedTests
{
    private static readonly Vector3[] Route = [Vector3.Zero, new(300, 0, 0)];

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void ReturningOverrideCanCatchSixYalmRunnerAlongActualRoute(int fps)
    {
        var motion = new CompanionRouteMotion();
        var intent = new CompanionIntentFrame(CompanionIntentMode.Returning, 4, 0, true, 9);
        motion.Update(Route, 0, intent, 0, true);
        var previous = motion.Current;
        for (var i = 1; i <= fps * 12; ++i)
        {
            var now = (double)i / fps;
            var playerArc = (float)(6 * now);
            var next = motion.Update(Route, playerArc, intent, now, true);
            Assert.Equal(PathTrail.At(Route, next.Arc), next.Position);
            Assert.InRange(Math.Abs(next.Velocity), 0, CompanionRouteMotion.MaximumSpeed + .001f);
            Assert.InRange(Math.Abs(next.Velocity - previous.Velocity), 0,
                CompanionRouteMotion.Acceleration / fps + .001f);
            Assert.InRange(Math.Abs(next.Arc - previous.Arc), 0,
                CompanionRouteMotion.MaximumSpeed / fps + .001f);
            if (now > 6) Assert.InRange(next.Arc - playerArc, .5f, 5);
            previous = next;
        }
    }

    [Theory]
    [InlineData(CompanionIntentMode.Returning, 4.5f)]
    [InlineData(CompanionIntentMode.Waiting, 3f)]
    [InlineData(CompanionIntentMode.Lead, 3.5f)]
    public void OmittedAndZeroHintsPreserveEachExistingDefault(CompanionIntentMode mode, float expected)
    {
        var omitted = new CompanionIntentFrame(mode, 4, 0, true);
        Assert.Equal(0, omitted.CruiseSpeed);
        var explicitZero = omitted with { CruiseSpeed = 0 };
        var first = new CompanionRouteMotion();
        var second = new CompanionRouteMotion();
        first.Update(Route, 0, omitted, 0, true);
        second.Update(Route, 0, explicitZero, 0, true);
        for (var i = 1; i <= 120; ++i)
        {
            var next = first.Update(Route, 100, omitted, i / 60.0, true);
            Assert.Equal(next, second.Update(Route, 100, explicitZero, i / 60.0, true));
        }
        Assert.InRange(first.Current.Velocity, expected - .01f, expected + .01f);
    }

    [Theory]
    [InlineData(CompanionIntentMode.Lead)]
    [InlineData(CompanionIntentMode.Returning)]
    [InlineData(CompanionIntentMode.Waiting)]
    public void EveryModeUsesFiniteBoundedOverrideWithSmoothAcceleration(CompanionIntentMode mode)
    {
        var motion = new CompanionRouteMotion();
        var intent = new CompanionIntentFrame(mode, 4, 0, true, 9);
        motion.Update(Route, 0, intent, 0, true);
        var previous = motion.Current;
        for (var i = 1; i <= 120; ++i)
        {
            var next = motion.Update(Route, 100, intent, i / 60.0, true);
            Assert.InRange(Math.Abs(next.Velocity - previous.Velocity), 0,
                CompanionRouteMotion.Acceleration / 60 + .001f);
            Assert.InRange(next.Velocity, 0, 9);
            previous = next;
        }
        Assert.InRange(previous.Velocity, 8.99f, 9);
        intent = intent with { CruiseSpeed = 1 };
        for (var i = 121; i <= 240; ++i)
        {
            var next = motion.Update(Route, 100, intent, i / 60.0, true);
            Assert.InRange(Math.Abs(next.Velocity - previous.Velocity), 0,
                CompanionRouteMotion.Acceleration / 60 + .001f);
            previous = next;
        }
        Assert.InRange(previous.Velocity, .99f, 1.01f);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(.999f)]
    [InlineData(9.001f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidHintHoldsWithoutRebasingOrCreatingCatchUpBacklog(float invalid)
    {
        var motion = new CompanionRouteMotion();
        var valid = new CompanionIntentFrame(CompanionIntentMode.Returning, 4, 0, true, 9);
        motion.Update(Route, 10, valid, 0, true);
        motion.Update(Route, 100, valid, .1, true);
        var before = motion.Current;
        Vector3[] shifted = [new(5, 0, 0), new(300, 0, 0)];
        for (var i = 1; i <= 10; ++i)
        {
            var held = motion.Update(shifted, 100, valid with { CruiseSpeed = invalid }, i, true);
            Assert.Equal(before.Position, held.Position);
            Assert.Equal(before.Arc, held.Arc);
            Assert.Same(Route, held.Route);
            Assert.False(held.NeedsConnection);
            Assert.Equal(0, held.Velocity);
        }
        var resumed = motion.Update(Route, 100, valid, 10.05, true);
        Assert.InRange(resumed.Arc - before.Arc, 0, .011f);
        Assert.InRange(resumed.Velocity, 0, CompanionRouteMotion.Acceleration * .05f + .001f);
    }

    [Fact]
    public void InvalidHintCannotSeedAnUnspawnedGuideAtItsTarget()
    {
        var motion = new CompanionRouteMotion();
        var invalid = new CompanionIntentFrame(CompanionIntentMode.Returning, 4, 0, true, float.NaN);
        Assert.False(motion.Update(Route, 100, invalid, 0, true).HasPosition);
    }
}
