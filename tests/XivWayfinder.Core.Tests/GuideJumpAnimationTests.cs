using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XivWayfinder.Core.Tests;

public sealed class GuideJumpAnimationTests
{
    [Theory]
    [InlineData("Jump")]
    [InlineData("Hop")]
    [InlineData("Standing Jump")]
    [InlineData("Forward_Jump")]
    [InlineData("RunningJump")]
    public void ExplicitCompleteAuthoredJumpIsEligible(string name)
    {
        Assert.Equal(1, GuideJumpAnimation.ImportedClip([new("Idle", 1), new(name, .8f)]));
    }

    [Theory]
    [InlineData("JumpStart", .8f)]
    [InlineData("JumpLoop", .8f)]
    [InlineData("JumpEnd", .8f)]
    [InlineData("Falling", .8f)]
    [InlineData("JumpAttack", .8f)]
    [InlineData("Jump", .1f)]
    [InlineData("Jump", 10f)]
    [InlineData("Jump", float.NaN)]
    public void UnknownOrPartialAnimationDoesNotInventJumpCapability(string name, float duration)
    {
        Assert.Equal(-1, GuideJumpAnimation.ImportedClip([new(name, duration)]));
    }

    [Fact]
    public void ExactPoseMustFitApprovedBodyEvenBetweenPremeasuredFrames()
    {
        var cap = new GuideJumpCapability(GuideJumpAnimationKind.ImportedClip, 0, "Jump", .8f, .2f, .6f);
        Vector3[] pose = [new(-.1f, 3, 0), new(.1f, 3.5f, 0)];
        Assert.True(GuideJumpAnimation.PoseFits(pose, 1, cap, out var minimum));
        Assert.Equal(3, minimum); // Root height is cancelled, not applied twice.
        Assert.False(GuideJumpAnimation.PoseFits(pose, 2, cap, out _));
        pose[0] = pose[0] with { X = .2001f };
        Assert.False(GuideJumpAnimation.PoseFits(pose, 1, cap, out _));
        pose[0] = new(float.NaN);
        Assert.False(GuideJumpAnimation.PoseFits(pose, 1, cap, out _));
        Assert.False(GuideJumpAnimation.PoseFits([], 1, cap, out _));
    }

    [Fact]
    public void NativePhasesRequireAllAuthoredClipsAndSelectByOwnHopPhase()
    {
        Assert.False(new GuideNativeJumpSet(31, 0, 33).Valid);
        var clips = new GuideNativeJumpSet(101, 102, 103);
        Assert.True(clips.Valid);
        Assert.Equal(101, clips.Timeline(GuideJumpPhase.Takeoff));
        Assert.Equal(102, clips.Timeline(GuideJumpPhase.Airborne));
        Assert.Equal(103, clips.Timeline(GuideJumpPhase.Landing));
        Assert.Equal(103, clips.Timeline(GuideJumpPhase.Complete));
    }

    [Fact]
    public void SupplementalFaceOverlayDoesNotInventOrVetoRigBodyCapability()
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "cbnm_jump_1" };
        Assert.True(GuideJumpAnimation.NativeBodyReferencesSupported(["cbnm_jump_1", "cfxf_clench"], names));
        Assert.False(GuideJumpAnimation.NativeBodyReferencesSupported(["cfxf_clench"], names));
        Assert.False(GuideJumpAnimation.NativeBodyReferencesSupported(["cbnm_jump_2", "cfxf_angry"], names));
        Assert.False(GuideJumpAnimation.NativeBodyReferencesSupported(["cbnm_jump_1", "unknown_body"], names));
        Assert.False(GuideJumpAnimation.NativeBodyReferencesSupported([], names));
    }

    [Fact]
    public void TimelineMetadataResolvesBoundedRelativeAnimationNameWithoutExecutingIt()
    {
        var bytes = Timeline("chara/animation/cbnm_jump_start.pap");
        Assert.Equal(["cbnm_jump_start"], GuideJumpAnimation.TimelineAnimationNames(bytes));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12 + 32), int.MaxValue);
        Assert.Empty(GuideJumpAnimation.TimelineAnimationNames(bytes));
    }

    [Fact]
    public void MalformedTimelineHeadersChunksAndStringsFailClosed()
    {
        var original = Timeline("cbnm_jump");
        foreach (var length in new[] { 0, 11, 20, original.Length - 1 })
            Assert.Empty(GuideJumpAnimation.TimelineAnimationNames(original.AsSpan(0, length)));
        var bytes = original.ToArray(); bytes[0] = 0;
        Assert.Empty(GuideJumpAnimation.TimelineAnimationNames(bytes));
        bytes = original.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 39);
        Assert.Empty(GuideJumpAnimation.TimelineAnimationNames(bytes));
        bytes = original.ToArray(); bytes[^1] = 0xff;
        Assert.Empty(GuideJumpAnimation.TimelineAnimationNames(bytes));
        bytes = original.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 4097);
        Assert.Empty(GuideJumpAnimation.TimelineAnimationNames(bytes));
    }

    private static byte[] Timeline(string reference)
    {
        var text = Encoding.ASCII.GetBytes(reference + '\0');
        var bytes = new byte[12 + 40 + text.Length];
        "TMLB"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        "C010"u8.CopyTo(bytes.AsSpan(12)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44), 32); // chunk start + 8 + 32 = 52
        text.CopyTo(bytes, 52);
        return bytes;
    }
}

public sealed partial class GuideMeshAnimationTests
{
    [Fact]
    public void JumpMetadataMeasuresAuthoredPosesWithHorizontalRootMotionRemoved()
    {
        using var file = new GlbFixture();
        file.Clip("Jump", 0, "translation", [0, .4f, .8f], [0, 0, 0, 3, 2, 4, 6, 0, 8]);
        var mesh = file.Load(); var capability = GuideJumpAnimation.MeasureImported(mesh);
        Assert.Equal(GuideJumpAnimationKind.ImportedClip, capability.Kind);
        Assert.Equal(0, capability.AnimationId); Assert.Equal(.8f, capability.Duration);
        Assert.InRange(capability.Radius, .59f, .61f);
        Assert.InRange(capability.Height, 1.17f, 1.18f);
        var scaled = capability with { Radius = capability.Radius * .3f, Height = capability.Height * .3f };
        Assert.True(scaled.Valid);
        var pose = new Vector3[mesh.Vertices.Length];
        mesh.SamplePose(0, .4f, pose, loop: false, inPlace: true);
        Assert.True(GuideJumpAnimation.PoseFits(pose, .3f, scaled, out var minimum));
        Assert.Equal(1.5f, minimum);
    }

    [Fact]
    public void JumpMetadataIsCancellableAndDoesNotGuessFromUnrelatedMovement()
    {
        using var file = new GlbFixture();
        file.Clip("Walk", 0, "translation", [0, 1], [0, 0, 0, 1, 0, 0]);
        Assert.Equal(default, GuideJumpAnimation.MeasureImported(file.Load()));
        using var jump = new GlbFixture();
        jump.Clip("Jump", 0, "translation", [0, 1], [0, 0, 0, 0, 1, 0]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => GuideJumpAnimation.MeasureImported(jump.Load(), cancellation.Token));
    }
}
