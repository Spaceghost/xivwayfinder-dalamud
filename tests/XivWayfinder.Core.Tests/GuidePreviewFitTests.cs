using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuidePreviewFitTests
{
    [Fact]
    public void WideBossFitsItsWingsEvenWhenItsBodyHeightLooksSmall()
    {
        Assert.True(GuidePreviewFit.TryFit(.3f, new Vector3(15, 2, 4), out var scale));
        Assert.Equal(.03f, scale, 5);
        Assert.Equal(1.5f, 15 * scale / .3f, 5);
    }

    [Fact]
    public void FitConsumesWorldExtentsWithoutApplyingScaleAgain()
    {
        Assert.True(GuidePreviewFit.TryFit(2, new Vector3(2, 6, 1), out var scale));
        Assert.Equal(.5f, scale);
        Assert.Equal(1.5f, 6 * scale / 2);
    }

    [Fact]
    public void UnscaledNativeBossBoundsFitOnceAtTheRequestedPreviewScale()
    {
        Assert.True(GuidePreviewFit.TryMeasureNativeBounds(new(-16, 0, -2), new(16, 10, 2),
            new(.3f), 1, 1, out var size));
        Assert.Equal(9.6f, size.X, 5);
        Assert.True(GuidePreviewFit.TryFit(.3f, size, out var scale));
        Assert.Equal(.046875f, scale, 6);
        Assert.Equal(1.5f, 32 * scale, 5);
    }

    [Fact]
    public void NativeFactorsAndNonuniformDrawScaleAreAppliedWithoutWorldTranslation()
    {
        Assert.True(GuidePreviewFit.TryMeasureNativeBounds(new(98, 10, -44), new(102, 18, -36),
            new(.5f, 1, 2), 1.5f, .5f, out var size));
        Assert.Equal(new Vector3(1.5f, 6, 12), size);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(-1, 1, 1)]
    [InlineData(float.NaN, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, -1)]
    [InlineData(1, float.PositiveInfinity, 1)]
    [InlineData(1, float.MaxValue, float.MaxValue)]
    public void UnreadyOrInvalidNativeScalingCannotChooseAPreviewSize(float scale, float first, float second)
    {
        Assert.False(GuidePreviewFit.TryMeasureNativeBounds(Vector3.Zero, new(32, 2, 4),
            new(scale), first, second, out var size));
        Assert.Equal(Vector3.Zero, size);
    }

    [Fact]
    public void SmallCompanionsRetainTheirSuggestedScale()
    {
        Assert.True(GuidePreviewFit.TryFit(1.5f, new Vector3(.4f, .8f, .5f), out var scale));
        Assert.Equal(1.5f, scale);
    }

    [Fact]
    public void VeryLargeModelsStopAtTheSupportedMinimumScale()
    {
        Assert.True(GuidePreviewFit.TryFit(.1f, new Vector3(100, 100, 100), out var scale));
        Assert.Equal(GuidePreviewFit.MinimumScale, scale);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-1, 2, 3)]
    [InlineData(float.NaN, 2, 3)]
    [InlineData(2, float.PositiveInfinity, 3)]
    public void MissingOrInvalidBoundsKeepTheOriginalScale(float x, float y, float z)
    {
        Assert.False(GuidePreviewFit.TryFit(.3f, new Vector3(x, y, z), out var scale));
        Assert.Equal(.3f, scale);
    }
}
