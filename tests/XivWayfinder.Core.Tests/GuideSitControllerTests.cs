using System.Numerics;
using System.Text.Json.Nodes;

namespace XivWayfinder.Core.Tests;

public sealed class GuideSitControllerTests
{
    private static readonly GuideSitClips Clips = new(10, 11, 12, .3f, .4f, .5f);
    private static readonly GuideGestureFrame Sit = new(GuideGestureKind.SitGround, 1, 0);

    [Fact]
    public void CompleteAuthoredSequenceHoldsMovementThroughFinalStandingSample()
    {
        var controller = new GuideSitController();
        var sample = controller.Update(Clips, Sit, .1f, true);
        Assert.Equal(GuideSitPhase.Enter, sample.Phase); Assert.True(sample.Restart); Assert.False(controller.CanMove);
        for (var i = 0; i < 3; i++) sample = controller.Update(Clips, Sit, .1f, false);
        Assert.Equal(GuideSitPhase.Seated, sample.Phase); Assert.Equal(11, sample.Clip); Assert.False(controller.CanMove);
        sample = controller.Update(Clips, null, .1f, false);
        Assert.Equal(GuideSitPhase.Exit, sample.Phase); Assert.Equal(0, sample.Time); Assert.True(sample.Restart);
        for (var i = 0; i < 5; i++) sample = controller.Update(Clips, null, .1f, false);
        Assert.Equal(GuideSitPhase.Exit, sample.Phase); Assert.Equal(.5f, sample.Time); Assert.Equal(0, sample.Rate);
        Assert.False(controller.CanMove); // Exact standing endpoint still held at accepted root.
        Assert.False(controller.Update(Clips, null, .1f, false).Active); Assert.True(controller.CanMove);
    }

    [Fact]
    public void CancelDuringEnterFinishesEnterThenUsesExitNeverReversesOrSnaps()
    {
        var controller = new GuideSitController(); controller.Update(Clips, Sit, .1f, true);
        var sample = controller.Update(Clips, new(GuideGestureKind.Beckon, 1, .1f), .1f, false);
        Assert.Equal(GuideSitPhase.Enter, sample.Phase); Assert.False(controller.CanMove);
        for (var i = 0; i < 2; i++) sample = controller.Update(Clips, null, .1f, false);
        Assert.Equal(GuideSitPhase.Exit, sample.Phase); Assert.Equal(12, sample.Clip);
    }

    [Fact]
    public void DifferentSequenceMustFullyStandBeforeStartingAnotherSit()
    {
        var controller = new GuideSitController(); controller.Update(Clips, Sit, .1f, true);
        for (var i = 0; i < 3; i++) controller.Update(Clips, Sit, .1f, false);
        var next = Sit with { SequenceId = 2 };
        Assert.Equal(GuideSitPhase.Exit, controller.Update(Clips, next, .1f, true).Phase);
        for (var i = 0; i < 5; i++) controller.Update(Clips, next, .1f, true);
        Assert.False(controller.CanMove);
        Assert.False(controller.Update(Clips, next, .1f, true).Active); Assert.True(controller.CanMove);
        Assert.Equal(GuideSitPhase.Enter, controller.Update(Clips, next, .1f, true).Phase);
    }

    [Fact]
    public void PausesRepeatedTicksAndClockGapsCannotFastForwardOrReleaseMovement()
    {
        var controller = new GuideSitController(); controller.Update(Clips, Sit, .1f, true);
        var paused = controller.Update(Clips, Sit with { Paused = true }, .1f, false);
        Assert.Equal(.1f, paused.Time); Assert.Equal(0, paused.Rate); // preceding native interval is accounted once
        for (var i = 0; i < 20; i++) Assert.Equal(.1f, controller.Update(Clips, Sit with { Paused = true }, .1f, false).Time);
        Assert.Equal(.1f, controller.Update(Clips, Sit, 0, false).Time);
        Assert.Equal(.1f, controller.Update(Clips, Sit, 10, false).Time);
        Assert.Equal(.1f, controller.Update(Clips, Sit, float.NaN, false).Time);
        Assert.False(controller.CanMove);
    }

    [Fact]
    public void RendererPlacementHoldStopsClockWithoutDroppingMovementGate()
    {
        var controller = new GuideSitController(); controller.Update(Clips, Sit, .1f, true);
        controller.Hold();
        Assert.Equal(0, controller.Update(Clips, Sit, .1f, false).Time);
        Assert.False(controller.CanMove);
        controller.Reset(); Assert.True(controller.CanMove); // actor was hidden/destroyed
    }

    [Fact]
    public void MissingChangingOrInvalidClipsNeverFabricateStandUp()
    {
        var controller = new GuideSitController();
        Assert.False(controller.Update(default, Sit, .1f, true).Active);
        Assert.False(controller.Update(Clips, Sit, .1f, false).Active);
        Assert.False(controller.Update(Clips, Sit with { Paused = true }, .1f, true).Active);
        controller.Update(Clips, Sit, .1f, true);
        for (var i = 0; i < 30; i++)
        { Assert.Equal(0, controller.Update(default, null, .1f, false).Rate); Assert.False(controller.CanMove); }
        Assert.False((Clips with { Exit = Clips.Enter }).Valid);
        Assert.False((Clips with { ExitSeconds = float.PositiveInfinity }).Valid);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void PlaybackDurationsRemainBoundedAcrossFrameRates(int fps)
    {
        var controller = new GuideSitController(); var dt = 1f / fps;
        controller.Update(Clips, Sit, dt, true);
        var elapsed = 0f; GuideSitSample sample;
        do { sample = controller.Update(Clips, Sit, dt, false); elapsed += dt; } while (sample.Phase == GuideSitPhase.Enter && elapsed < 1);
        Assert.InRange(elapsed, Clips.EnterSeconds - .00001f, Clips.EnterSeconds + dt + .00001f);
        sample = controller.Update(Clips, null, dt, false); elapsed = 0;
        while (!controller.CanMove && elapsed < 2) { sample = controller.Update(Clips, null, dt, false); elapsed += dt; }
        Assert.InRange(elapsed, Clips.ExitSeconds, Clips.ExitSeconds + 2 * dt + .00001f);
    }

    [Fact]
    public void ImportedCapabilityRequiresVerifiedTripleNotAmbiguousSitOrNamesAlone()
    {
        GuideAnimationClip[] clips = [new("Idle", 2), new("GroundSitStart", 1), new("GroundSitIdle", 2), new("GroundSitEnd", 1)];
        var candidate = GuideSitAnimation.Find(clips); Assert.True(candidate.Valid);
        var discovered = GuideGestureAnimation.Imported(clips);
        Assert.False(discovered.Capabilities.SitGround);
        Assert.True((discovered with { Sitting = candidate }).Capabilities.SitGround);
        Assert.False(GuideSitAnimation.Find([new("Sit", 1), new("SittingIdle", 2), new("StandUp", 1)]).Valid);
        Assert.False(GuideSitAnimation.Find(clips[..3]).Valid);
    }

    [Fact]
    public void ImportedEndpointProofRejectsDifferentRootOrPoseAndMalformedGeometry()
    {
        Vector3[] pose = [new(0, 0, 0), new(0, 1, 0), new(.3f, .5f, 0)];
        Assert.True(GuideSitAnimation.EndpointsMatch(pose, pose));
        Assert.True(GuideSitAnimation.EndpointsMatch(pose, pose.Select(p => p + new Vector3(.01f, 0, 0)).ToArray()));
        Assert.False(GuideSitAnimation.EndpointsMatch(pose, pose.Select(p => p + new Vector3(.1f, 0, 0)).ToArray()));
        Assert.False(GuideSitAnimation.EndpointsMatch(pose, [pose[0], new(float.NaN), pose[2]]));
        Assert.False(GuideSitAnimation.EndpointsMatch(pose, pose[..2]));
        Assert.False(GuideSitAnimation.EndpointsMatch([], []));
    }
}

public sealed partial class GuideMeshAnimationTests
{
    [Fact]
    public void ImportedSitRequiresAnActualAuthoredRoundTripWithCompatibleEndpoints()
    {
        using var file = new GlbFixture();
        AppendSitClip(file, "Idle", 1, 1);
        AppendSitClip(file, "GroundSitStart", 1, .5f);
        AppendSitClip(file, "GroundSitIdle", .5f, .5f);
        AppendSitClip(file, "GroundSitEnd", .5f, 1);
        var clips = GuideSitAnimation.MeasureImported(file.Load());
        Assert.True(clips.Valid); Assert.Equal(1, clips.Enter); Assert.Equal(2, clips.Loop); Assert.Equal(3, clips.Exit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImportedSitRejectsInertClipsAndExitThatDoesNotReturnToStanding(bool inert)
    {
        using var file = new GlbFixture(); var seated = inert ? 1f : .5f;
        AppendSitClip(file, "Idle", 1, 1);
        AppendSitClip(file, "GroundSitStart", 1, seated);
        AppendSitClip(file, "GroundSitIdle", seated, seated);
        AppendSitClip(file, "GroundSitEnd", seated, seated);
        Assert.False(GuideSitAnimation.MeasureImported(file.Load()).Valid);
    }

    private static void AppendSitClip(GlbFixture file, string name, float from, float to)
    {
        var clips = file.Root["animations"]?.DeepClone().AsArray() ?? new JsonArray();
        file.Clip(name, 0, "scale", [0, 1], [1, from, 1, 1, to, 1]);
        clips.Add(file.Root["animations"]![0]!.DeepClone());
        file.Root["animations"] = clips;
    }
}
