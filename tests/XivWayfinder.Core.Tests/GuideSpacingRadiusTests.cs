using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuideSpacingRadiusTests
{
    private static readonly int[] Triangle = [0, 1, 2];

    [Fact]
    public void ImportedRadiusIncludesBindAndCurrentPoseAroundTheirAuthoredRoot()
    {
        var spacing = new GuideSpacingRadius();
        Vector3[] bind = [new(-.3f, 0, -.4f), new(.1f, 2, .1f), Vector3.Zero];
        Vector3[] pose = [new(-.1f, 0, -.1f), new(.6f, 2, .8f), Vector3.Zero];
        Assert.True(spacing.TryMeasureImported(bind, pose, Triangle, 2, out var radius));
        Assert.InRange(radius, 2.25f, 2.250001f);
        Assert.True(spacing.TryMeasureImported(bind, bind, Triangle, 2, out var afterNarrowing));
        Assert.Equal(radius, afterNarrowing);
    }

    [Fact]
    public void UnreferencedOutliersAndMalformedUnusedVerticesDoNotEnlargeSpacing()
    {
        var spacing = new GuideSpacingRadius();
        Vector3[] bind = [new(-.3f, 0, -.4f), Vector3.Zero, new(.1f, 0, .1f), new(1e20f), new(float.NaN)];
        Assert.True(spacing.TryMeasureImported(bind, [], Triangle, 1, out var radius));
        Assert.InRange(radius, .60f, .600001f);
    }

    [Fact]
    public void SmallPoseJitterAndNarrowerPosesKeepThePublishedRadiusStable()
    {
        var spacing = new GuideSpacingRadius();
        foreach (var measured in new[] { .4f, .4001f, .3999f, .401f, .3f })
        {
            Assert.True(spacing.TryRetain(measured, Vector3.One, out var radius));
            Assert.Equal(.5f, radius);
        }
        Assert.True(spacing.TryRetain(.6f, Vector3.One, out var expanded));
        Assert.Equal(.75f, expanded);
        Assert.True(spacing.TryRetain(.4f, Vector3.One, out var contracted));
        Assert.Equal(expanded, contracted);
    }

    [Fact]
    public void ScaleAndAssetChangesDiscardThePreviousAssetsObservedEnvelope()
    {
        var spacing = new GuideSpacingRadius();
        Assert.True(spacing.TryRetain(4, Vector3.One, out var large));
        Assert.True(spacing.TryRetain(.4f, new(.1f), out var resized));
        Assert.True(resized < large);
        Assert.Equal(.5f, resized);
        Assert.True(spacing.TryRetain(4, new(.1f), out _));
        spacing.Reset();
        Assert.True(spacing.TryRetain(.4f, new(.1f), out var reselected));
        Assert.Equal(.5f, reselected);
    }

    [Fact]
    public void NativeAsymmetricBoundsUseWorldOriginAndBothScaleFactors()
    {
        var root = new Vector3(100, 10, -40);
        Assert.True(GuideNativeBounds.TryMeasureRootEnvelope(root + new Vector3(-.1f, 0, -.1f),
            root + new Vector3(.4f, 1, .3f), root, new(2), .5f, .5f, out var body));
        var spacing = new GuideSpacingRadius();
        Assert.True(spacing.TryRetain(body.Radius, new(.5f), out var radius));
        Assert.InRange(radius, .35f, .350001f);
    }

    [Fact]
    public void ReflectedOffsetsAndYawCannotEscapeThePaddedRadius()
    {
        Vector3[] bind = [new(-.2f, -10, .8f), new(.5f, 10, -.3f), new(.1f, 0, .1f)];
        var spacing = new GuideSpacingRadius();
        Assert.True(spacing.TryMeasureImported(bind, [], Triangle, 1.7f, out var radius));
        for (var i = 0; i < 32; i++)
        foreach (var point in bind)
        {
            var rotated = Vector3.Transform(point * 1.7f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, i * MathF.Tau / 32));
            Assert.True(Math.Sqrt((double)rotated.X * rotated.X + (double)rotated.Z * rotated.Z) < radius);
        }
    }

    [Fact]
    public void TiltedTallNativeBodyIncludesHeightProjectedIntoHorizontalSpacing()
    {
        Assert.True(GuideNativeBounds.TryMeasureRootEnvelope(new(-.05f, -.3f, -.05f),
            new(.05f, 2, .05f), Vector3.Zero, Vector3.One, 1, 1, out var envelope));
        var rotation = Quaternion.CreateFromYawPitchRoll(.7f, MathF.PI / 4, .2f);
        Assert.True(GuideSpacingRadius.TryMeasureRotatedNative(envelope, rotation, out var radius));
        Assert.True(radius > 1.4f);
        foreach (var x in new[] { -.05f, .05f })
        foreach (var y in new[] { -.3f, 2 })
        foreach (var z in new[] { -.05f, .05f })
        {
            var point = Vector3.Transform(new(x, y, z), rotation);
            Assert.True(Math.Sqrt((double)point.X * point.X + (double)point.Z * point.Z) <= radius);
        }
        Assert.True(GuideSpacingRadius.TryMeasureRotatedNative(envelope,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f), out var upright));
        Assert.Equal(envelope.Radius, upright);
        Assert.False(GuideSpacingRadius.TryMeasureRotatedNative(envelope, default, out _));
    }

    [Fact]
    public void TiltedImportedBodyMeasuresItsActualReferencedPose()
    {
        Vector3[] vertices = [new(-.05f, 0, -.05f), new(.05f, 2, .05f), new(.05f, 0, -.05f)];
        var spacing = new GuideSpacingRadius();
        Assert.True(spacing.TryMeasureImported(vertices, vertices, Triangle, 1, out var upright));
        Assert.True(spacing.TryMeasureImported(vertices, vertices, Triangle, 1, out var tilted,
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4)));
        Assert.True(tilted > 1.5f);
        Assert.True(tilted > upright * 5);
        Assert.False(spacing.TryMeasureImported(vertices, [], Triangle, 1, out _, new Quaternion(0, 0, 0, 2)));
    }

    [Fact]
    public void InvalidSamplesFailWithoutPoisoningTheRetainedEnvelope()
    {
        var spacing = new GuideSpacingRadius();
        Assert.True(spacing.TryRetain(.4f, Vector3.One, out var prior));
        Assert.False(spacing.TryRetain(float.NaN, Vector3.One, out _));
        Assert.False(spacing.TryRetain(-1, Vector3.One, out _));
        Assert.False(spacing.TryRetain(float.MaxValue, Vector3.One, out _));
        Assert.False(spacing.TryRetain(.1f, new(0, 1, 1), out _));
        Assert.False(spacing.TryRetain(.1f, new(1, float.PositiveInfinity, 1), out _));
        Assert.True(spacing.TryRetain(.1f, Vector3.One, out var stillPrior));
        Assert.Equal(prior, stillPrior);

        Vector3[] vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ];
        Assert.False(spacing.TryMeasureImported(vertices, [], [-1, 1, 2], 1, out _));
        Assert.False(spacing.TryMeasureImported(vertices, [], [0, 1, 3], 1, out _));
        Assert.False(spacing.TryMeasureImported(vertices, [Vector3.Zero], Triangle, 1, out _));
        Assert.False(spacing.TryMeasureImported(vertices, [], Triangle, 0, out _));
        Assert.False(spacing.TryMeasureImported(vertices, [], [], 1, out _));
        vertices[1] = new(float.NaN);
        Assert.False(spacing.TryMeasureImported(vertices, [], Triangle, 1, out _));
    }

    [Fact]
    public void RepeatedImportedMeasurementsDoNotAllocatePerVertexOrFrame()
    {
        var spacing = new GuideSpacingRadius();
        Vector3[] vertices = [new(-.3f, 0, -.4f), Vector3.Zero, new(.1f, 0, .1f)];
        for (var i = 0; i < 100; i++) spacing.TryMeasureImported(vertices, vertices, Triangle, 1, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) spacing.TryMeasureImported(vertices, vertices, Triangle, 1, out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
