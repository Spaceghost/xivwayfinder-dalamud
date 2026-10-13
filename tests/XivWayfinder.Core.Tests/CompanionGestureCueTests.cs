using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionGestureCueTests
{
    [Fact]
    public void RefusedPoseCannotAdvanceAnAcceptedGesture()
    {
        var cue = new CompanionGestureCue();
        var start = cue.Propose(GuideGestureKind.Beckon, 1)!.Value;
        cue.Resolve(true);
        Assert.Equal(.1f, cue.Propose(GuideGestureKind.Beckon, 1.1)!.Value.ElapsedSeconds, 5);
        var held = cue.Resolve(false)!.Value;
        Assert.Equal(start.ElapsedSeconds, held.ElapsedSeconds); Assert.True(held.Paused);
        Assert.Equal(.05f, cue.Propose(GuideGestureKind.Beckon, 1.15)!.Value.ElapsedSeconds, 5);
    }

    [Fact]
    public void MissingAcknowledgementRejectsThePriorProposal()
    {
        var cue = new CompanionGestureCue();
        cue.Propose(GuideGestureKind.Wait, 0); cue.Resolve(true);
        cue.Propose(GuideGestureKind.Wait, .1);
        Assert.Equal(.05f, cue.Propose(GuideGestureKind.Wait, .15)!.Value.ElapsedSeconds, 5);
    }

    [Fact]
    public void NewKindAndLifecycleHaveNewSequencesButARejectedFirstPoseHasNoGesture()
    {
        var cue = new CompanionGestureCue();
        var first = cue.Propose(GuideGestureKind.Wait, 0)!.Value;
        Assert.Null(cue.Resolve(false));
        var next = cue.Propose(GuideGestureKind.Wait, .1)!.Value; cue.Resolve(true);
        Assert.True(next.SequenceId > first.SequenceId);
        var sit = cue.Propose(GuideGestureKind.SitGround, .2)!.Value; cue.Resolve(true);
        Assert.True(sit.SequenceId > next.SequenceId); Assert.Equal(0, sit.ElapsedSeconds);
        cue.Reset();
        Assert.True(cue.Propose(GuideGestureKind.SitGround, .3)!.Value.SequenceId > sit.SequenceId);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    [InlineData(2d)]
    public void InvalidOrDiscontinuousClockHoldsWithoutBankingTime(double now)
    {
        var cue = new CompanionGestureCue();
        cue.Propose(GuideGestureKind.Wait, 0); cue.Resolve(true);
        var frame = cue.Propose(GuideGestureKind.Wait, now)!.Value;
        Assert.Equal(0, frame.ElapsedSeconds); Assert.True(frame.Paused);
        cue.Resolve(true);
        var resumed = cue.Propose(GuideGestureKind.Wait, double.IsFinite(now) ? now+.05 : .05)!.Value;
        Assert.Equal(.05f, resumed.ElapsedSeconds, 5);
    }

    [Fact]
    public void ExitRequestClearsTheGestureAndPauseDoesNotConsumeClipTime()
    {
        var cue = new CompanionGestureCue();
        cue.Propose(GuideGestureKind.SitGround, 0); cue.Resolve(true);
        Assert.Equal(0, cue.Propose(GuideGestureKind.SitGround, .1, paused:true)!.Value.ElapsedSeconds);
        cue.Resolve(true);
        Assert.Null(cue.Propose(null, .2));
        Assert.Null(cue.Resolve(true));
        Assert.Null(cue.Propose((GuideGestureKind)99, .3));
    }
}
