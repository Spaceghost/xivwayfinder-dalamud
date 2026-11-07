using System.Buffers.Binary;

namespace XivWayfinder.Core.Tests;

public sealed class GuideGestureTests
{
    [Fact]
    public void ImportedGesturesRequireTheirOwnExplicitlyNamedAuthoredClips()
    {
        var clips = GuideGestureAnimation.Imported([new("Idle", 2), new("GroundSit", 3), new("Beckon", 4)]);
        Assert.True(clips.Capabilities.Wait); Assert.True(clips.Capabilities.Beckon);
        Assert.Equal(1, clips.SitGround);
        Assert.False(clips.Capabilities.SitGround); // no movement gate/stand-up yet
        Assert.Equal(0, clips.Clip(GuideGestureKind.Wait)); Assert.Equal(2, clips.Clip(GuideGestureKind.Beckon));
    }

    [Theory]
    [InlineData("Jump")]
    [InlineData("Animation 1")]
    [InlineData("Wave")]
    [InlineData("BeckonAttack")]
    [InlineData("Sit")]
    [InlineData("SittingIdle")]
    public void UnknownActionsAndAmbiguousChairSitNeverInventCapabilities(string name)
    {
        var clips = GuideGestureAnimation.Imported([new(name, 2)]);
        Assert.Equal(new GuideImportedGestures(-1, -1, -1), clips);
    }

    [Fact]
    public void InvalidOrUnboundedImportedClipDurationIsUnsupported()
    {
        Assert.False(GuideGestureAnimation.Imported([new("Beckon", float.NaN)]).Capabilities.Beckon);
        Assert.False(GuideGestureAnimation.Imported([new("Beckon", 0)]).Capabilities.Beckon);
        Assert.False(GuideGestureAnimation.Imported([new("Idle", 1000)]).Capabilities.Wait);
    }

    [Fact]
    public void ChangingKindRestartsEvenWhenCallerReusesSequenceId()
    {
        var clock = new GuideGestureClock();
        var frame = new GuideGestureFrame(GuideGestureKind.Wait, 1, 0);
        Assert.True(clock.Update(frame, .02f).Restart);
        Assert.False(clock.Update(frame with { ElapsedSeconds = .02f }, .02f).Restart);
        var changed = clock.Update(frame with { Kind = GuideGestureKind.Beckon, ElapsedSeconds = .04f }, .02f);
        Assert.True(changed.Restart); Assert.Equal(0, changed.Time);
        Assert.True(clock.Update(frame with { Kind = GuideGestureKind.Beckon, SequenceId = 2 }, .02f).Restart);
    }

    [Fact]
    public void PausedRepeatedOrSuspendedFramesDoNotAccumulatePlaybackBacklog()
    {
        var clock = new GuideGestureClock();
        var frame = new GuideGestureFrame(GuideGestureKind.Beckon, 1, 0);
        clock.Update(frame, .02f);
        Assert.Equal(.02f, clock.Update(frame with { ElapsedSeconds = .02f }, .02f).Time);
        Assert.Equal(0, clock.Update(frame with { ElapsedSeconds = .02f }, .02f).Rate);
        Assert.Equal(.02f, clock.Update(frame with { ElapsedSeconds = .2f, Paused = true }, .02f).Time);
        Assert.InRange(clock.Update(frame with { ElapsedSeconds = .22f }, .02f).Time, .0399f, .0401f);
        Assert.InRange(clock.Update(frame with { ElapsedSeconds = 10 }, .02f).Time, .0399f, .0401f);
        Assert.InRange(clock.Update(frame with { ElapsedSeconds = 10.02f }, .02f).Time, .0599f, .0601f);
    }

    [Fact]
    public void InvalidFramesClearOwnedPlaybackAndCannotSelectUnknownAction()
    {
        var clock = new GuideGestureClock();
        Assert.False(new GuideGestureFrame((GuideGestureKind)900, 1, 0).Valid);
        Assert.False(new GuideGestureFrame(GuideGestureKind.Wait, -1, 0).Valid);
        Assert.False(new GuideGestureFrame(GuideGestureKind.Wait, 0, float.NaN).Valid);
        clock.Update(new(GuideGestureKind.Wait, 0, 0), .02f);
        Assert.Equal(default, clock.Update(new(GuideGestureKind.Wait, 0, float.NaN), .02f));
        Assert.True(clock.Update(new(GuideGestureKind.Wait, 0, 0), .02f).Restart);
    }

    [Fact]
    public void NativeDurationRequiresBoundedTmdhMetadataAndUsesThirtyHzConvention()
    {
        var bytes = new byte[28];
        "TMLB"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 28);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        "TMDH"u8.CopyTo(bytes.AsSpan(12)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(24), 140);
        Assert.Equal(140 / 30f, GuideGestureAnimation.TimelineSeconds(bytes));
        Assert.Equal(0, GuideGestureAnimation.TimelineSeconds(bytes.AsSpan(0, 27)));
        bytes[12] = 0; Assert.Equal(0, GuideGestureAnimation.TimelineSeconds(bytes));
    }
}
