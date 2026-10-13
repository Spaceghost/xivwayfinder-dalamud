using XivVisualAssets.Animation;

namespace XivWayfinder.Core.Tests;

public sealed class VisualAnimationStateTests
{
    private static readonly LocomotionClips Clips = new(0, 1, 2);
    private static VisualAnimationState State() => new([2f, 1f, .5f]);
    private static VisualAnimationFrame Settle(VisualAnimationState state, float speed)
    {
        VisualAnimationFrame frame = default;
        for (var i = 0; i < 8; i++) frame = state.Update(Clips, speed, .1);
        return frame;
    }

    [Fact]
    public void IdleWalkAndRunUseDifferentEntryAndExitThresholds()
    {
        var state = State();
        Assert.Equal(0, Settle(state, .18f).ToClip);
        Assert.Equal(1, Settle(state, .3f).ToClip);
        Assert.Equal(1, Settle(state, .1f).ToClip);
        Assert.Equal(0, Settle(state, .05f).ToClip);
        Assert.Equal(1, Settle(state, 3.1f).ToClip);
        Assert.Equal(2, Settle(state, 3.4f).ToClip);
        Assert.Equal(2, Settle(state, 2.9f).ToClip);
        Assert.Equal(1, Settle(state, 2.5f).ToClip);
    }

    [Fact]
    public void PinnedClipIsIntentionalEvenWhileMovingAndKeepsAuthoredRate()
    {
        var state = State();
        state.Update(Clips, 9, 0, pinnedClip: 0);
        var frame = state.Update(Clips, 9, .1, pinnedClip: 0);
        Assert.Equal(0, frame.ToClip);
        Assert.Equal(.1f, frame.ToTime, 5);
        Assert.False(frame.Blending);
    }

    [Fact]
    public void ClipClocksScaleWithMovementAndStayBoundedByDuration()
    {
        var slow = State(); var fast = State();
        slow.Update(Clips, .9f, 0); fast.Update(Clips, 1.8f, 0);
        Assert.Equal(.05f, slow.Update(Clips, .9f, .1).ToTime, 5);
        Assert.Equal(.1f, fast.Update(Clips, 1.8f, .1).ToTime, 5);
        for (var i = 0; i < 1000; i++) fast.Update(Clips, 1.8f, .1);
        Assert.InRange(fast.Update(Clips, 1.8f, .1).ToTime, 0, 1);
    }

    [Fact]
    public void FadeStartsAtOldPoseThenConvergesWithoutResettingBothClocks()
    {
        var state = State(); state.Update(Clips, 0, 0);
        var start = state.Update(Clips, 1.8f, .1);
        Assert.Equal(0, start.FromClip); Assert.Equal(1, start.ToClip); Assert.Equal(0, start.Blend);
        var middle = state.Update(Clips, 1.8f, .09);
        Assert.Equal(.5f, middle.Blend, 5); Assert.True(middle.ToTime > 0);
        var end = state.Update(Clips, 1.8f, .09);
        Assert.Equal(1, end.FromClip); Assert.Equal(1, end.ToClip); Assert.False(end.Blending);
    }

    [Fact]
    public void ReversingAFadePreservesTheSameMixtureAtZeroDelta()
    {
        var state = State(); state.Update(Clips, 0, 0); state.Update(Clips, 1.8f, 0);
        var before = state.Update(Clips, 1.8f, .06);
        var after = state.Update(Clips, 0, 0);
        Assert.Equal(before.FromClip, after.ToClip); Assert.Equal(before.ToClip, after.FromClip);
        Assert.Equal(before.FromTime, after.ToTime); Assert.Equal(before.ToTime, after.FromTime);
        Assert.Equal(1 - before.Blend, after.Blend, 5);
    }

    [Fact]
    public void ThirdClipWaitsForCurrentFadeRatherThanSnapping()
    {
        var state = State(); state.Update(Clips, 0, 0); state.Update(Clips, 1.8f, 0);
        var during = state.Update(Clips, 5, .05);
        Assert.Equal(0, during.FromClip); Assert.Equal(1, during.ToClip);
        Assert.InRange(during.Blend, 0.001f, .999f);
        Assert.Equal(2, Settle(state, 5).ToClip);
    }

    [Fact]
    public void WalkRunTransitionPreservesNormalizedCyclePhase()
    {
        var state = State(); state.Update(Clips, 1.8f, 0); state.Update(Clips, 1.8f, .1);
        var frame = state.Update(Clips, 4.5f, 0);
        Assert.Equal(1, frame.FromClip); Assert.Equal(2, frame.ToClip);
        Assert.Equal(frame.FromTime / 1, frame.ToTime / .5f, 5);
    }

    [Fact]
    public void DisabledAndStaticModelsNeverInventMotion()
    {
        var state = State(); Settle(state, 5);
        Assert.Equal(new VisualAnimationFrame(-1, 0, -1, 0, 1), state.Update(Clips, 5, .1, animate: false));
        var empty = new VisualAnimationState([]);
        Assert.Equal(-1, empty.Update(new(-1, -1, -1), 5, .1).ToClip);
        Assert.Equal(0, state.Update(new(0, -1, -1), 5, .1).ToClip);
    }

    [Fact]
    public void NonfiniteAndNegativeInputsCannotPoisonClocks()
    {
        var state = State(); state.Update(Clips, 0, 0);
        Assert.Equal(0, state.Update(Clips, float.NaN, double.NaN).ToTime);
        Assert.Equal(0, state.Update(Clips, -3, -1).ToTime);
        Assert.Equal(.1f, state.Update(Clips, 0, 99).ToTime, 5);
        Assert.Equal(.1f, state.Update(Clips, float.PositiveInfinity, double.PositiveInfinity).ToTime, 5);
    }

    [Fact]
    public void InvalidPoliciesDurationsAndClipIndicesFailEarly()
    {
        Assert.Throws<ArgumentException>(() => new VisualAnimationState([float.NaN]));
        Assert.Throws<ArgumentException>(() => new VisualAnimationState([1f], new(StartSpeed: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => State().Update(new(0, 1, 3), 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => State().Update(Clips, 0, 0, pinnedClip: 3));
    }
}
