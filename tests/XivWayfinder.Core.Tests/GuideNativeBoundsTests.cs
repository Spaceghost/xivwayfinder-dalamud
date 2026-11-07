using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuideNativeBoundsTests
{
    [Fact]
    public void UprightPreflightAcceptsYawButNotPitchRollOrStaleRoot()
    {
        foreach (var yaw in new[] { 0f, .7f, MathF.PI, -2f })
            Assert.True(GuideNativeBounds.MatchesUprightRoot(Vector3.Zero, Vector3.Zero,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)));
        Assert.True(GuideNativeBounds.MatchesUprightRoot(new(GuideNativeBounds.RootTolerance, 0, 0), Vector3.Zero, Quaternion.Identity));
        Assert.False(GuideNativeBounds.MatchesUprightRoot(new(.0201f, 0, 0), Vector3.Zero, Quaternion.Identity));
        Assert.False(GuideNativeBounds.MatchesUprightRoot(Vector3.Zero, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, .0001f)));
        Assert.False(GuideNativeBounds.MatchesUprightRoot(Vector3.Zero, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .1f)));
        Assert.False(GuideNativeBounds.MatchesUprightRoot(Vector3.Zero, Vector3.Zero, default));
        Assert.False(GuideNativeBounds.MatchesUprightRoot(Vector3.Zero, Vector3.Zero, new(0, 0, 0, 2)));
        Assert.False(GuideNativeBounds.MatchesUprightRoot(Vector3.Zero, Vector3.Zero, new(0, float.NaN, 0, 1)));
        Assert.False(GuideNativeBounds.MatchesUprightRoot(new(float.NaN), Vector3.Zero, Quaternion.Identity));
        Assert.False(GuideNativeBounds.MatchesUprightRoot(Vector3.Zero, new(float.PositiveInfinity), Quaternion.Identity));
    }

    [Theory]
    [InlineData(.2f)]
    [InlineData(1)]
    [InlineData(3)]
    public void ResourceOffsetsFollowRequestedScaleExactlyOnce(float scale)
    {
        Assert.True(GuideNativeBounds.TryMeasureRootEnvelope(new(-.2f, 0, -.15f), new(.2f, 1, .15f),
            Vector3.Zero, new(scale), 1, 1, out var body));
        Assert.InRange(body.Radius, .25f * scale, .25f * scale + .00001f);
        Assert.InRange(body.Height, scale, scale + .00001f);
        Assert.InRange(body.MinimumY, -.00001f, 0);
    }

    [Fact]
    public void BothNativeFactorsAndNonuniformAxesAffectTheRootEnvelope()
    {
        var root = new Vector3(100, 10, -40);
        Assert.True(GuideNativeBounds.TryMeasureRootEnvelope(root + new Vector3(-2, 0, -4), root + new Vector3(2, 8, 4),
            root, new(.5f, 1, 2), 1.5f, .5f, out var body));
        Assert.InRange(body.Radius, MathF.Sqrt(.75f * .75f + 6 * 6), 6.047f);
        Assert.InRange(body.Height, 6, 6.00001f);
        Assert.True(GuideNativeBounds.TryMeasureSize(root + new Vector3(-2, 0, -4), root + new Vector3(2, 8, 4),
            new(.5f, 1, 2), 1.5f, .5f, out var size));
        Assert.Equal(new Vector3(1.5f, 6, 12), size);
    }

    [Fact]
    public void WorldTranslationIsNeverMultipliedByModelScale()
    {
        var root = new Vector3(100, 20, -50);
        Assert.True(GuideNativeBounds.TryMeasureRootEnvelope(new(-2, .5f, -1), new(2, 3, 1), Vector3.Zero,
            new(.5f), 2, 1, out var local));
        Assert.True(GuideNativeBounds.TryMeasureRootEnvelope(root + new Vector3(-2, .5f, -1), root + new Vector3(2, 3, 1), root,
            new(.5f), 2, 1, out var translated));
        Assert.Equal(local, translated);
    }

    [Fact]
    public void ElevatedBaselineDoesNotShrinkRootToHeadClearance()
    {
        Assert.True(GuideNativeBounds.TryMeasureRootEnvelope(new(-.1f, .4f, -.1f), new(.1f, 1, .1f), Vector3.Zero,
            new(2), 1, 1, out var body));
        Assert.InRange(body.Height, 2, 2.00001f);
        Assert.InRange(body.MinimumY, .79999f, .8f);
    }

    [Fact]
    public void BelowRootGeometryIsReportedAfterScalingForTheCallersFloorPolicy()
    {
        Assert.True(GuideNativeBounds.TryMeasureRootEnvelope(new(-.1f, -.02f, -.1f), new(.1f, .5f, .1f), Vector3.Zero,
            new(3), 1, 1, out var body));
        Assert.True(body.MinimumY < -GuideJumpPolicy.FootClearance);
        Assert.InRange(body.Height, 1.56f, 1.56001f);
    }

    [Fact]
    public void AsymmetricBoundsUseFarthestCornersRatherThanHalfWidth()
    {
        Assert.True(GuideNativeBounds.TryMeasureRootEnvelope(new(-.1f, 0, -.1f), new(.4f, 1, .3f), Vector3.Zero,
            Vector3.One, 1, 1, out var body));
        Assert.InRange(body.Radius, .5f, .50001f);
        for (var step = 0; step < 64; step++)
        {
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, step * MathF.Tau / 64);
            foreach (var x in new[] { -.1f, .4f })
                foreach (var z in new[] { -.1f, .3f })
                {
                    var point = Vector3.Transform(new(x, 1, z), yaw);
                    Assert.True(new Vector2(point.X, point.Z).Length() <= body.Radius + .000001f);
                }
        }
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(-1, 1, 1)]
    [InlineData(float.NaN, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, -1)]
    [InlineData(1, float.PositiveInfinity, 1)]
    [InlineData(1, float.MaxValue, float.MaxValue)]
    [InlineData(3, 1e-20f, float.MaxValue)] // native intermediate overflows despite a finite double final product
    [InlineData(.25f, float.MaxValue, float.Epsilon)] // native intermediate is zero despite a nonzero double product
    public void InvalidOrUnrepresentableScalingFailsClosed(float scale, float first, float second)
    {
        Assert.False(GuideNativeBounds.TryMeasureRootEnvelope(new(-1, 0, -1), Vector3.One, Vector3.Zero,
            new(scale), first, second, out var body));
        Assert.Equal(default, body);
        Assert.False(GuideNativeBounds.TryMeasureSize(new(-1, 0, -1), Vector3.One, new(scale), first, second, out var size));
        Assert.Equal(Vector3.Zero, size);
    }

    [Fact]
    public void InvalidBoundsOriginAndDegenerateBodiesFailClosed()
    {
        Assert.False(GuideNativeBounds.TryMeasureRootEnvelope(Vector3.One, Vector3.Zero, Vector3.Zero, Vector3.One, 1, 1, out _));
        Assert.False(GuideNativeBounds.TryMeasureRootEnvelope(Vector3.Zero, Vector3.One, new(float.NaN), Vector3.One, 1, 1, out _));
        Assert.False(GuideNativeBounds.TryMeasureRootEnvelope(Vector3.Zero, new(float.PositiveInfinity), Vector3.Zero, Vector3.One, 1, 1, out _));
        Assert.False(GuideNativeBounds.TryMeasureRootEnvelope(Vector3.Zero, Vector3.UnitY, Vector3.Zero, Vector3.One, 1, 1, out _));
        Assert.False(GuideNativeBounds.TryMeasureRootEnvelope(-Vector3.UnitX, Vector3.UnitX, Vector3.Zero, Vector3.One, 1, 1, out _));
    }

    [Fact]
    public void NonfiniteSingleAxisAndCollapsedPositiveDimensionsFailClosed()
    {
        Assert.False(GuideNativeBounds.TryMeasureRootEnvelope(new(-1, 0, -1), Vector3.One, Vector3.Zero,
            new(1, float.NaN, 1), 1, 1, out _));
        Assert.False(GuideNativeBounds.TryMeasureRootEnvelope(new(-float.Epsilon, 0, -float.Epsilon), new(float.Epsilon), Vector3.Zero,
            new(.25f), 1, 1, out _));
    }
}
