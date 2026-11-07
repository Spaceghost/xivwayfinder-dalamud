using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using XivVisualAssets.Animation;

namespace XivWayfinder.Core.Tests;

public sealed partial class GuideMeshAnimationTests
{
    [Fact]
    public void BlendEndpointsExactlyMatchExistingClipAndBindSampling()
    {
        using var file = new GlbFixture();
        file.Clip("travel", 0, "translation", [0, 1], [3, 0, 0, 8, 2, 0]);
        var mesh = file.Load(); var expected = new Vector3[3]; var actual = new Vector3[3];
        foreach (var loop in new[] { false, true })
        foreach (var inPlace in new[] { false, true })
        foreach (var time in new[] { -1.25f, 0, .25f, 1, 2.5f })
        {
            mesh.SamplePose(0, time, expected, loop, inPlace);
            mesh.SampleBlendedPose(new(0, time, -1, 0, 0), actual, loop, inPlace);
            Assert.Equal(expected, actual);
            mesh.SampleBlendedPose(new(-1, 0, 0, time, 1), actual, loop, inPlace);
            Assert.Equal(expected, actual);
            mesh.SampleBlendedPose(new(0, time, -1, 0, 1), actual, loop, inPlace);
            Assert.Equal(mesh.Vertices, actual);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossfadeSlerpsLocalBonesBeforeSkinningWithoutShrinkingGeometry(bool skin)
    {
        using var file = new GlbFixture();
        var node = 0;
        if (skin)
        {
            node = 1;
            file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"skin":0},{}]""");
            file.Root["scenes"] = JsonNode.Parse("""[{"nodes":[0,1]}]""");
            file.Root["skins"] = JsonNode.Parse("""[{"joints":[1]}]""");
            file.Attributes["JOINTS_0"] = file.Bytes(new byte[12], "VEC4", false);
            file.Attributes["WEIGHTS_0"] = file.Floats([1,0,0,0, 1,0,0,0, 1,0,0,0], "VEC4");
        }
        var half = MathF.Sqrt(.5f);
        file.Clip("turn", node, "rotation", [0, 1], [0,0,0,1, 0,0,half,half]);
        var mesh = file.Load(); var pose = new Vector3[3];
        mesh.SampleBlendedPose(new(-1, 0, 0, 1, .5f), pose, loop: false);
        Near(new(half, half, 0), pose[1] - pose[0]);
        Assert.Equal(1, Vector3.Distance(pose[0], pose[1]), 5);
    }

    [Fact]
    public void BlendingSamplesBothIndependentClocksAndMorphWeightsBeforeTransforms()
    {
        using var file = new GlbFixture();
        var target = file.Floats([0,0,0, 0,0,0, 0,2,0], "VEC3");
        file.Primitive["targets"] = JsonSerializer.SerializeToNode(new[] { new { POSITION = target } });
        file.Root["nodes"]![0]!["scale"] = JsonNode.Parse("[1,2,1]");
        file.Clip("one", 0, "weights", [0,1], [0,1]);
        AddSecondClip(file, "two", 0, "weights", [0,1], [1,0]);
        var mesh = file.Load(); var pose = new Vector3[3];
        mesh.SampleBlendedPose(new(0, .25f, 1, .5f, .5f), pose);
        Near(mesh.Vertices[2] + new Vector3(0, .75f, 0), pose[2]); // .375 weight * 2 delta * 2 scale / 2 extent.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InPlaceBlendRetainsAuthoredOffsetsAndVerticalMotionUnderParentAxes(bool rotated)
    {
        using var file = new GlbFixture();
        var half = MathF.Sqrt(.5f);
        file.Root["nodes"] = JsonSerializer.SerializeToNode(new object[]
        {
            new { rotation = rotated ? new[] {half,0f,0f,half} : new[] {0f,0f,0f,1f}, scale = new[] {2f,2f,2f}, children = new[] {1} },
            new { mesh = 0, translation = new[] {3f,4f,5f} }
        });
        file.Clip("one", 1, "translation", [0,1], [3,4,5, 4,6,8]);
        AddSecondClip(file, "two", 1, "translation", [0,1], [7,8,9, 9,12,15]);
        var mesh = file.Load(); var first = new Vector3[3]; var second = new Vector3[3]; var pose = new Vector3[3];
        mesh.SamplePose(0, 1, first, loop:false, inPlace:true);
        mesh.SamplePose(1, 1, second, loop:false, inPlace:true);
        mesh.SampleBlendedPose(new(0, 1, 1, 1, .25f), pose, loop:false, inPlace:true);
        for (var i = 0; i < pose.Length; i++) Near(Vector3.Lerp(first[i], second[i], .25f), pose[i]);
    }

    [Fact]
    public void InPlaceBlendFromBindPoseDoesNotStripInitialAuthoredOffset()
    {
        using var file = new GlbFixture();
        file.Clip("travel", 0, "translation", [0,1], [4,0,2, 12,2,8]);
        var mesh = file.Load(); var pose = new Vector3[3];
        mesh.SampleBlendedPose(new(-1, 0, 0, 1, .5f), pose, loop:false, inPlace:true);
        for (var i = 0; i < pose.Length; i++) Near(mesh.Vertices[i] + new Vector3(2,1,1), pose[i]);
    }

    [Fact]
    public void BlendValidationRejectsNonfiniteAndOutOfRangeFramesEvenAtUnusedEndpoint()
    {
        using var file = new GlbFixture(); file.Clip("idle", 0, "translation", [0,1], [0,0,0, 0,0,0]);
        var mesh = file.Load(); var pose = new Vector3[3];
        foreach (var frame in new VisualAnimationFrame[]
        {
            new(0, 0, 1, 0, 0), new(-2, 0, 0, 0, 1), new(0, float.NaN, 0, 0, 1),
            new(0, 0, 0, float.PositiveInfinity, 0), new(0, 0, 0, 0, float.NaN),
            new(0, 0, 0, 0, -.1f), new(0, 0, 0, 0, 1.1f)
        }) Assert.Throws<ArgumentOutOfRangeException>(() => mesh.SampleBlendedPose(frame, pose));
        Assert.Throws<ArgumentException>(() => mesh.SampleBlendedPose(new(0, 0, 0, 0, .5f), new Vector3[2]));
    }

    private static void AddSecondClip(GlbFixture file, string name, int node, string property, float[] times, float[] values)
    {
        var first = file.Root["animations"]![0]!.DeepClone();
        file.Clip(name, node, property, times, values);
        file.Root["animations"]!.AsArray().Insert(0, first);
    }
}
