using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivWayfinder.Core.Tests;

public sealed partial class GuideMeshAnimationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PoseSnapshotReproducesAnActuallyBlendedPoseWithMorphsAndRootOffsets(bool loop, bool inPlace)
    {
        using var file = new GlbFixture();
        var half = MathF.Sqrt(.5f);
        file.Root["nodes"] = JsonSerializer.SerializeToNode(new object[]
        {
            new { rotation = new[] { half,0f,0f,half }, scale = new[] {2f,2f,2f}, children = new[] {1} },
            new { mesh = 0, translation = new[] {3f,4f,5f} }
        });
        var morph = file.Floats([0, 0, 0, 0, 0, 0, 0, 2, 0], "VEC3");
        file.Primitive["targets"] = JsonSerializer.SerializeToNode(new[] { new { POSITION = morph } });
        file.Clip("move", 1, "translation", [0, 1], [3, 4, 5, 6, 7, 10]);
        AddSecondClip(file, "morph", 1, "weights", [0, 1], [0, 1]);
        var mesh = file.Load(); var expected = new Vector3[3]; var actual = new Vector3[3];
        var expectedNormals = new Vector3[3]; var actualNormals = new Vector3[3];
        foreach (var t in new[] { -.3f, 0, .4f, 1.2f })
        {
            var frame = new XivVisualAssets.Animation.VisualAnimationFrame(0, t, 1, .6f, .37f);
            mesh.SampleBlendedPose(frame, expected, loop, inPlace, expectedNormals);
            var capture = mesh.CapturePose(frame, loop, inPlace);
            // Later animation activity must not overwrite captured pooled state.
            mesh.SamplePose(0, .95f, actual, loop, inPlace, actualNormals);
            mesh.SampleCapturedPose(capture, actual, actualNormals);
            for (var i = 0; i < 3; i++) { Near(expected[i], actual[i]); Near(expectedNormals[i], actualNormals[i]); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PoseSnapshotRecoveryEndpointsRemainExactAcrossFurtherInterruptedBlends(bool inPlace)
    {
        using var file = new GlbFixture();
        var half = MathF.Sqrt(.5f);
        file.Clip("turn", 0, "rotation", [0, 1], [0, 0, 0, 1, 0, 0, half, half]);
        var mesh = file.Load(); var expected = new Vector3[3]; var actual = new Vector3[3];
        var first = mesh.CapturePose(new(-1, 0, 0, .8f, .43f), loop: false, inPlace);
        mesh.SampleCapturedPose(first, expected);
        mesh.SampleFromCapture(first, 0, 1, 0, actual, loop: false);
        Assert.Equal(expected, actual);
        var interrupted = mesh.CaptureFromSnapshot(first, 0, 1, .52f, loop: false);
        mesh.SampleFromCapture(first, 0, 1, .52f, expected, loop: false);
        mesh.SampleCapturedPose(interrupted, actual);
        for (var i = 0; i < 3; i++) Near(expected[i], actual[i]);
        mesh.SampleFromCapture(interrupted, -1, 0, 0, actual, loop: false);
        for (var i = 0; i < 3; i++) Near(expected[i], actual[i]);
        mesh.SampleFromCapture(interrupted, -1, 0, 1, actual, loop: false);
        for (var i = 0; i < 3; i++) Near(mesh.Vertices[i], actual[i]);
        // Bone-space interpolation, not a vertex lerp that shrinks the limb.
        mesh.SampleFromCapture(interrupted, -1, 0, .5f, actual, loop: false);
        Assert.Equal(1, Vector3.Distance(actual[0], actual[1]), 5);
    }

    [Fact]
    public void PoseSnapshotRejectsAnotherRigAndInvalidRequestsBeforeWritingVertices()
    {
        using var file = new GlbFixture(); file.Clip("move", 0, "translation", [0, 1], [0, 0, 0, 1, 1, 0]);
        var first = file.Load(); var second = file.Load(); var output = new Vector3[3];
        var capture = first.CapturePose(new(0, .2f, 0, .2f, 1));
        Assert.Throws<ArgumentException>(() => second.SampleCapturedPose(capture, output));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.SampleFromCapture(capture, 0, 0, float.NaN, output));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.CaptureFromSnapshot(capture, 99, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.CapturePose(new(0, 0, 0, float.NaN, 1)));
        Assert.Throws<ArgumentException>(() => first.SampleCapturedPose(capture, new Vector3[2]));
        Assert.All(output, p => Assert.Equal(Vector3.Zero, p));
    }

    [Fact]
    public void PoseSnapshotCanExitToAnotherActuallyBlendedPoseWithoutReplacingItWithItsDestinationClip()
    {
        using var file = new GlbFixture(); var half = MathF.Sqrt(.5f);
        file.Clip("turn", 0, "rotation", [0, 1], [0, 0, 0, 1, 0, 0, half, half]);
        var mesh = file.Load(); var a = mesh.CapturePose(new(-1, 0, 0, .8f, .32f), inPlace: true);
        var b = mesh.CapturePose(new(-1, 0, 0, .4f, .7f), inPlace: true);
        var actual = new Vector3[3]; var expected = new Vector3[3];
        mesh.SampleCapturedPose(a, expected); mesh.SampleCapturedPose(mesh.BlendCapturedPoses(a, b, 0), actual);
        for (var i = 0; i < 3; i++) Near(expected[i], actual[i]);
        mesh.SampleCapturedPose(b, expected); mesh.SampleCapturedPose(mesh.BlendCapturedPoses(a, b, 1), actual);
        for (var i = 0; i < 3; i++) Near(expected[i], actual[i]);
        mesh.SampleCapturedPose(mesh.BlendCapturedPoses(a, b, .5f), actual);
        Assert.Equal(1, Vector3.Distance(actual[0], actual[1]), 5);
    }
}
