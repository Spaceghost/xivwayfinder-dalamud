using System.Numerics;
using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public sealed class FairyTrailTests
{
    private static readonly Vector3[] Corner = [Vector3.Zero, new(10, 0, 0), new(10, 0, 30)];

    [Fact]
    public void MotesStayOnThePolylineWithoutCuttingCorners()
    {
        var output = new FairyMote[200];
        var count = FairyTrail.Sample(Corner, 4, 30, 1.2, 2.4f, false, output);
        Assert.InRange(count, 10, FairyTrail.MaxMotes);
        foreach (var mote in output.Take(count))
        {
            Assert.True(mote.GroundPosition.Z == 0 || mote.GroundPosition.X == 10);
            Assert.InRange(mote.GroundPosition.X, 4, 10);
            Assert.InRange(mote.Lift, .025f, .155f);
            Assert.InRange(mote.Opacity, 0, 1);
            Assert.InRange(mote.Radius, .16f, .24f);
            Assert.InRange(mote.DustOpacity, 0, 1);
        }
    }

    [Fact]
    public void DeterministicAndReducedMotionFreezesBothDriftAndPulse()
    {
        var a = new FairyMote[96]; var b = new FairyMote[96];
        var n = FairyTrail.Sample(Corner, 0, 20, 2, 2.4f, true, a);
        var m = FairyTrail.Sample(Corner, 0, 20, 200, 2.4f, true, b);
        Assert.Equal(n, m); Assert.Equal(a, b);
        n = FairyTrail.Sample(Corner, 0, 20, 2, 2.4f, false, a);
        m = FairyTrail.Sample(Corner, 0, 20, 2, 2.4f, false, b);
        Assert.Equal(n, m); Assert.Equal(a, b);
    }

    [Fact]
    public void NeverExceedsOutputOrGlobalBudgetAndStopsAtDestination()
    {
        var small = new FairyMote[3];
        Assert.InRange(FairyTrail.Sample(Corner, 0, 5000, 1, 2, false, small), 1, 3);
        Assert.Equal(0, FairyTrail.Sample(Corner, 40, 20, 1, 2, false, small));
        Assert.Equal(0, FairyTrail.Sample(Corner, 0, 20, 1, 2, false, Span<FairyMote>.Empty));
    }

    [Theory]
    [InlineData(float.NaN, 1, 1)]
    [InlineData(0, double.NaN, 1)]
    [InlineData(0, 1, float.PositiveInfinity)]
    [InlineData(-1, 1, 1)]
    public void InvalidMotionIsRejected(float progress, double time, float period)
    {
        Assert.Equal(0, FairyTrail.Sample(Corner, progress, 20, time, period, false, new FairyMote[96]));
    }

    [Fact]
    public void LightsBeginAheadOfTheCompanionRatherThanUnderOrBehindIt()
    {
        var output = new FairyMote[96];
        var count = FairyTrail.Sample(Corner, 2, 20, 1, 2, false, output, startAhead: 4.45f);
        Assert.True(count > 0);
        foreach (var mote in output.Take(count))
            Assert.True(PathTrail.Project(Corner, mote.GroundPosition, out _) > 6.45f);
        Assert.Equal(0, FairyTrail.Sample(Corner, 0, 20, 1, 2, false, output, float.NaN));
    }

    [Fact]
    public void MissingOrRestingCompanionStillLeavesAVisibleRoute()
    {
        var output = new FairyMote[96];
        var start = FairyTrail.StartAhead(5, null);
        var n = FairyTrail.Sample(Corner, 5, 20, 1, 2, false, output, start);
        Assert.True(n > 10);
        Assert.InRange(PathTrail.Project(Corner, output[0].GroundPosition, out _) - 5, .12f, 1.1f);
        Assert.Equal(.12f, FairyTrail.StartAhead(5, 2)); // a guide waiting behind
        Assert.Equal(.12f, FairyTrail.StartAhead(5, float.NaN));
        Assert.Equal(4.45f, FairyTrail.StartAhead(5, 9));
    }

    [Fact]
    public void InteriorLightsNeverBlinkOutAndKeepTheirTerrainAnchors()
    {
        Vector3[] path = [Vector3.Zero, new(40, 0, 0)];
        var output = new FairyMote[96];
        Vector3[]? anchors = null;
        float? firstOpacity = null;
        var changed = false;
        for (var frame = 0; frame < 180; frame++)
        {
            var count = FairyTrail.Sample(path, 0, 20, frame / 60d, 2.4f, false, output);
            var interior = output.Take(count).Where(m => m.GroundPosition.X is > 2 and < 12).ToArray();
            Assert.True(interior.Length > 12);
            Assert.All(interior, mote => Assert.InRange(mote.Opacity, .30f, 1));
            var positions = interior.Select(m => m.GroundPosition).ToArray();
            if (anchors is not null) Assert.Equal(anchors, positions);
            else anchors = positions;
            if (firstOpacity is { } previous && Math.Abs(previous - interior[0].Opacity) > .05f) changed = true;
            firstOpacity ??= interior[0].Opacity;
        }
        Assert.True(changed); // a steady route still has a visible shimmer
    }

    [Fact]
    public void FloatingDustFadesOutBeforeItsLiftWraps()
    {
        Vector3[] path = [Vector3.Zero, new(40, 0, 0)];
        var output = new FairyMote[96];
        var lowFades = 0; var highFades = 0;
        for (var frame = 0; frame < 180; frame++)
        {
            var count = FairyTrail.Sample(path, 0, 20, frame / 60d, 2.4f, false, output);
            foreach (var mote in output.Take(count))
            {
                if (mote.Lift < .028f) { Assert.InRange(mote.DustOpacity, 0, .01f); lowFades++; }
                if (mote.Lift > .152f) { Assert.InRange(mote.DustOpacity, 0, .01f); highFades++; }
            }
        }
        Assert.True(lowFades > 0 && highFades > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnimatedTrailKeepsItsVerifiedTerrainWhileThePlayerRuns(bool overhead)
    {
        Vector3[] route = [Vector3.Zero, new(300, 30, 0)];
        var output = new FairyMote[FairyTrail.MaxMotes];
        var queries = 0;
        static float Terrain(float x) => x * .1f + .2f * MathF.Sin(x * 2);
        var sampler = new TrailSurfaceSampler((origin, _, _) =>
        {
            queries++;
            return new(new(origin.X, Terrain(origin.X), origin.Z), Vector3.UnitY, default, default, default);
        });
        // Six yalms/second for four seconds also crosses the successful floor
        // cache's hard deadline. A glow's lifetime must not move its floor key.
        for (var frame = 0; frame < 240; frame++)
        {
            var now = frame / 60d;
            var player = new Vector3(frame * .1f, Terrain(frame * .1f), 0);
            var progress = PathTrail.Project(route, player, out _);
            var count = FairyTrail.Sample(route, progress, 27, now, 2.4f, false, output, 4.45f);
            sampler.Begin(1);
            queries = 0;
            var placed = 0;
            foreach (var mote in output.AsSpan(0, count))
            {
                var ok = overhead
                    ? sampler.TryPlaceOverhead(mote.GroundPosition, player, 3, now, out var at)
                    : sampler.TryPlace(mote.GroundPosition, now, out at);
                if (!ok) continue;
                placed++;
                var minimum = Terrain(at.X) + TrailPlacement.GroundClearance;
                if (overhead) minimum += 3;
                Assert.True(at.Y >= minimum - .0001f);
            }
            Assert.InRange(queries, 0, 4);
            if (frame >= 60)
            {
                Assert.True(count >= 20);
                Assert.True(placed >= count * .95f,
                    $"Frame {frame}: only {placed}/{count} animated lights had verified ground.");
            }
        }
    }
}
