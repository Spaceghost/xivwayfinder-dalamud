using System.Numerics;

namespace Wayfinder.Core.Tests;

/// <summary>Animation timing and following vnavmesh's path corners.</summary>
public sealed class MotionAndPathTests
{
    [Fact]
    public void BreathCyclesOverItsPeriod()
    {
        Assert.Equal(0.5f, Pulse.Breath(0, 2.6f), 1e-5f);
        Assert.Equal(1f, Pulse.Breath(0.65, 2.6f), 1e-5f);
        Assert.Equal(0.5f, Pulse.Breath(1.3, 2.6f), 1e-5f);
        Assert.Equal(0f, Pulse.Breath(1.95, 2.6f), 1e-5f);
        // a long session does not drift
        const float period = 2.6f;
        Assert.Equal(Pulse.Breath(0.4, period), Pulse.Breath(0.4 + (double)period * 100_000, period), 1e-3f);
        Assert.Equal(0.5f, Pulse.Breath(double.NaN, 2.6f));
        Assert.Equal(0.5f, Pulse.Breath(1, 0f));
    }

    [Fact]
    public void BobStaysWithinItsAmplitude()
    {
        for (var t = 0.0; t < 5; t += 0.01)
            Assert.InRange(Pulse.Bob(t, 2.2f, 0.12f), -0.12f - 1e-6f, 0.12f + 1e-6f);
    }

    [Fact]
    public void GlowIsDimFacingAndBrightTurnedAway()
    {
        var facing = Pulse.Glow(0f, 0.5f, 0.28f, 1f);
        var sideways = Pulse.Glow(0.5f, 0.5f, 0.28f, 1f);
        var away = Pulse.Glow(1f, 0.5f, 0.28f, 1f);
        Assert.Equal(0.28f, facing, 1e-3f);
        Assert.True(sideways > 0.8f, $"sideways {sideways}");
        Assert.Equal(1f, away, 1e-3f);
        // a small wobble near the right way does not flicker
        Assert.Equal(facing, Pulse.Glow(0.02f, 0.5f, 0.28f, 1f), 1e-4f);
        // monotonic in between
        var last = 0f;
        for (var k = 0f; k <= 1f; k += 0.01f)
        {
            var g = Pulse.Glow(k, 0.5f, 0.28f, 1f);
            Assert.True(g >= last - 1e-6f);
            last = g;
        }

        // breathing moves it by at most ±15 %
        Assert.Equal(0.28f * 0.85f, Pulse.Glow(0f, 0f, 0.28f, 1f), 1e-4f);
        Assert.Equal(0.28f * 1.15f, Pulse.Glow(0f, 1f, 0.28f, 1f), 1e-4f);
    }

    [Fact]
    public void TapPushesQuicklyAndEasesBack()
    {
        Assert.Equal(0f, Pulse.Tap(0, 1.1f), 1e-5f);
        Assert.Equal(1f, Pulse.Tap(0.275, 1.1f), 1e-4f);
        Assert.True(Pulse.Tap(0.1, 1.1f) > Pulse.Tap(0.05, 1.1f));
        Assert.True(Pulse.Tap(0.6, 1.1f) < Pulse.Tap(0.4, 1.1f));
        Assert.Equal(0f, Pulse.Tap(1.1 - 1e-6, 1.1f), 1e-3f);
        Assert.Equal(0f, Pulse.Tap(1, -1f));
    }

    [Fact]
    public void TrailCrestTravelsOutward()
    {
        // at a quarter period the crest is a quarter of the way along
        var brightest = Enumerable.Range(0, 12).MaxBy(i => Pulse.Trail(i, 12, 0.45, 1.8f));
        Assert.Equal(2, brightest);
        var later = Enumerable.Range(0, 12).MaxBy(i => Pulse.Trail(i, 12, 1.2, 1.8f));
        Assert.True(later > brightest);
        Assert.Equal(0f, Pulse.Trail(12, 12, 0, 1.8f));
        Assert.Equal(0f, Pulse.Trail(0, 0, 0, 1.8f));
    }

    [Fact]
    public void ApproachIsFrameRateIndependent()
    {
        var a = 0f;
        for (var i = 0; i < 60; i++)
            a = Pulse.Approach(a, 1f, 1f / 60, 0.25f);
        var b = 0f;
        for (var i = 0; i < 144; i++)
            b = Pulse.Approach(b, 1f, 1f / 144, 0.25f);
        Assert.Equal(a, b, 1e-3f);
        Assert.Equal(1f - 0.0625f, a, 1e-3f); // four half-lives in a second
        Assert.Equal(0.5f, Pulse.Approach(0f, 1f, 0.25f, 0.25f), 1e-5f);
        Assert.Equal(1f, Pulse.Approach(float.NaN, 1f, 0.1f, 0.2f));
        Assert.Equal(0.3f, Pulse.Approach(0.3f, 1f, 0f, 0.2f));
    }

    private static readonly Vector3[] Path =
    [
        new(0, 0, 0),
        new(10, 0, 0),
        new(10, 0, 10),
        new(20, 5, 10),
    ];

    [Fact]
    public void PointsAtTheEndOfTheSegmentThePlayerIsOn()
    {
        Assert.Equal(1, PathFollow.NextCorner(Path, new Vector3(3, 0, 0.5f), 1.5f));
        Assert.Equal(2, PathFollow.NextCorner(Path, new Vector3(10.5f, 0, 4), 1.5f));
        Assert.Equal(3, PathFollow.NextCorner(Path, new Vector3(15, 3, 10), 1.5f));
    }

    [Fact]
    public void ACornerWithinReachIsSkipped()
    {
        Assert.Equal(2, PathFollow.NextCorner(Path, new Vector3(9.2f, 0, 0.3f), 1.5f));
        // but never past the last one
        Assert.Equal(3, PathFollow.NextCorner(Path, new Vector3(19.5f, 0, 10), 1.5f));
    }

    [Fact]
    public void CuttingACornerNeverPointsBackwards()
    {
        // standing inside the bend, nearer the second leg
        Assert.Equal(2, PathFollow.NextCorner(Path, new Vector3(8, 0, 5), 1.5f));
    }

    [Fact]
    public void TinyPaths()
    {
        Assert.Equal(-1, PathFollow.NextCorner([], Vector3.Zero, 1.5f));
        Assert.Equal(0, PathFollow.NextCorner([new Vector3(3, 0, 3)], Vector3.Zero, 1.5f));
        Assert.Equal(float.PositiveInfinity, PathFollow.DistanceFromPath([], Vector3.Zero));
        Assert.Equal(5f, PathFollow.DistanceFromPath(Path, new Vector3(5, 0, -5)), 1e-4f);
    }

    [Fact]
    public void RequeriesOnlyWhenStaleAndNotTooOften()
    {
        var goal = new Vector3(20, 0, 10);
        Assert.True(PathFollow.ShouldRequery(false, false, 99, 2, goal, goal, 0, 6));
        Assert.False(PathFollow.ShouldRequery(false, true, 99, 2, goal, goal, 0, 6));
        Assert.False(PathFollow.ShouldRequery(false, false, 1, 2, goal, goal, 0, 6));
        Assert.False(PathFollow.ShouldRequery(true, false, 99, 2, goal, goal, 3, 6));
        Assert.True(PathFollow.ShouldRequery(true, false, 99, 2, goal, goal, 7, 6));
        Assert.True(PathFollow.ShouldRequery(true, false, 99, 2, goal, goal + new Vector3(2, 0, 0), 0, 6));
        // the target's height alone does not count
        Assert.False(PathFollow.ShouldRequery(true, false, 99, 2, goal, goal + new Vector3(0, 30, 0), 0, 6));
    }
}
