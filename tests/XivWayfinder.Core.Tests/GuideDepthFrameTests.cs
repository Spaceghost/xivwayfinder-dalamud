using XivWayfinder.Shared;

namespace XivWayfinder.Core.Tests;

public sealed class GuideDepthFrameTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedDepthKeepsNearOpaqueGuideInEitherHookOrder(bool guideFirst)
    {
        var frame = new GuideDepthFrame();
        uint presentCount = 0;
        var pixel = new Pixel(frame);
        frame.BeginFrame(1, ++presentCount);
        if (guideFirst) { pixel.Draw("Fox", 4); pixel.Draw("Rug", 5); }
        else { pixel.Draw("Rug", 5); pixel.Draw("Fox", 4); }
        Assert.Equal("Fox", pixel.Color);
        Assert.Equal(1, pixel.Clears);
    }

    [Fact]
    public void RugAloneInNextFrameDoesNotKeepDepartedGuideSilhouette()
    {
        var frame = new GuideDepthFrame();
        uint presentCount = 0;
        var pixel = new Pixel(frame);
        frame.BeginFrame(1, ++presentCount);
        pixel.Draw("Fox", 4);
        frame.BeginFrame(1, ++presentCount); // independent of active imported/preview model
        pixel.Draw("Rug", 5);
        Assert.Equal("Rug", pixel.Color);
        Assert.Equal(2, pixel.Clears);
        frame.BeginFrame(1, ++presentCount); // an entirely empty frame
        frame.BeginFrame(1, ++presentCount);
        pixel.Draw("Rug", 6);
        Assert.Equal("Rug", pixel.Color);
        Assert.Equal(3, pixel.Clears);
    }

    [Fact]
    public void NearRugFoldOccludesGuideAndTransparentBatchDoesNotWriteDepth()
    {
        var frame = new GuideDepthFrame();
        uint presentCount = 0;
        var pixel = new Pixel(frame);
        frame.BeginFrame(1, ++presentCount);
        pixel.Draw("Rug fold", 3);
        pixel.Draw("Fox", 4);
        Assert.Equal("Rug fold", pixel.Color);
        frame.BeginFrame(1, ++presentCount);
        pixel.Draw("Transparent effect", 2, writesDepth: false);
        pixel.Draw("Fox", 4);
        Assert.Equal("Fox", pixel.Color); // preserves existing BLEND write-zero policy
    }

    [Fact]
    public void ResourceReplacementRequiresFreshClearWithinSameEpoch()
    {
        var frame = new GuideDepthFrame();
        uint presentCount = 0;
        frame.BeginFrame(1, ++presentCount);
        frame.MarkCleared();
        Assert.False(frame.NeedsClear);
        frame.ResourceChanged(); // resize/device replacement has undefined contents
        Assert.True(frame.NeedsClear);
        frame.MarkCleared();
        Assert.False(frame.NeedsClear);
    }

    [Fact]
    public void UnarmedDisposedAndReloadedProviderCannotExposeOldFrame()
    {
        var old = new GuideDepthFrame();
        Assert.False(old.CanAcquire);
        old.BeginFrame(1, 20); old.MarkCleared(); old.Dispose();
        old.BeginFrame(1, 21); old.ResourceChanged(); old.MarkCleared();
        Assert.False(old.CanAcquire);
        Assert.False(old.NeedsClear);
        var reloaded = new GuideDepthFrame();
        Assert.False(reloaded.CanAcquire);
        reloaded.BeginFrame(1, 21);
        Assert.True(reloaded.CanAcquire);
        Assert.True(reloaded.NeedsClear);
    }

    [Fact]
    public void FailedClearRemainsPendingForNextConsumer()
    {
        var frame = new GuideDepthFrame();
        uint presentCount = 0;
        frame.BeginFrame(1, ++presentCount);
        Assert.True(frame.NeedsClear);
        // A failed device/context operation must not acknowledge the clear.
        Assert.True(frame.NeedsClear);
        frame.MarkCleared();
        Assert.False(frame.NeedsClear);
        Assert.Equal("XivWayfinder.v1.AcquireWorldDepth", IpcContract.AcquireWorldDepth);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UiFallbackOrderCannotClearBetweenTwoConsumers(bool rugFirst)
    {
        var frame = new GuideDepthFrame();
        var pixel = new Pixel(frame);
        frame.BeginFrame(12, 99);
        pixel.Draw("Old guide", 1);
        // Next presented frame. Each consumer observes the same counter,
        // including when another plugin's UiBuilder subscriber lies between.
        frame.BeginFrame(12, 100);
        if (rugFirst) pixel.Draw("Rug", 5); else pixel.Draw("Fox", 4);
        frame.BeginFrame(12, 100);
        frame.BeginFrame(12, 100);
        if (rugFirst) pixel.Draw("Fox", 4); else pixel.Draw("Rug", 5);
        Assert.Equal("Fox", pixel.Color);
        Assert.Equal(2, pixel.Clears);
    }

    [Fact]
    public void NativeDrawAndLaterUiFallbackUseSamePresentationDepth()
    {
        var frame = new GuideDepthFrame();
        var pixel = new Pixel(frame);
        frame.BeginFrame(12, 100); // native shader boundary
        pixel.Draw("Fox", 4);
        frame.BeginFrame(12, 100); // UiBuilder fallback, before original Present
        pixel.Draw("Rug", 5);
        Assert.Equal("Fox", pixel.Color);
        Assert.Equal(1, pixel.Clears);
        frame.BeginFrame(12, 101);
        pixel.Draw("Rug", 5);
        Assert.Equal("Rug", pixel.Color);
        Assert.Equal(2, pixel.Clears);
    }

    [Fact]
    public void CounterWrapAndSwapchainReplacementBothClearOldContents()
    {
        var frame = new GuideDepthFrame();
        var pixel = new Pixel(frame);
        frame.BeginFrame(12, uint.MaxValue);
        pixel.Draw("Old guide", 1);
        frame.BeginFrame(12, 0);
        pixel.Draw("Rug", 5);
        Assert.Equal("Rug", pixel.Color);
        frame.BeginFrame(13, 0); // new swapchain may start at the same count
        pixel.Draw("New guide", 6);
        Assert.Equal("New guide", pixel.Color);
        Assert.Equal(3, pixel.Clears);
    }

    [Fact]
    public void MissingSwapchainCannotAuthorizeAcquisition()
    {
        var frame = new GuideDepthFrame();
        Assert.False(frame.BeginFrame(0, 123));
        Assert.False(frame.CanAcquire);
        Assert.False(frame.NeedsClear);
    }

    // Single-pixel oracle for the actual shader depth convention and D32
    // LESS_EQUAL state; host coverage cannot prove native COM/device behavior.
    private sealed class Pixel(GuideDepthFrame frame)
    {
        private float depth;
        public int Clears { get; private set; }
        public string Color { get; private set; } = "Game scene";
        public void Draw(string color, float distance, bool writesDepth = true)
        {
            Assert.True(frame.CanAcquire);
            if (frame.NeedsClear) { depth = 1; Clears++; frame.MarkCleared(); }
            var incoming = distance / (1 + distance);
            if (incoming > depth) return;
            Color = color;
            if (writesDepth) depth = incoming;
        }
    }
}
