using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivWayfinder.Core.Tests;

public sealed partial class GuideMeshAnimationTests
{
    [Fact]
    public void TranslationRetainsBindNormalizationAndSupportsClampingLoopingAndIndependentSamples()
    {
        using var file = new GlbFixture();
        file.Clip("walk", 0, "translation", [0, 1], [0, 0, 0, 2, 0, 0]);
        var mesh = file.Load();
        Assert.Equal("walk", Assert.Single(mesh.Animations).Name);
        Assert.Equal(1, mesh.Animations[0].DurationSeconds);
        var pose = new Vector3[3];
        mesh.SamplePose(0, .5f, pose);
        Near(new(.5f, -.5f, 0), pose[0]);
        mesh.SamplePose(0, 100, pose, false);
        Near(new(1.5f, -.5f, 0), pose[0]);
        mesh.SamplePose(0, 1.25f, pose);
        Near(new(0, -.5f, 0), pose[0]);
        mesh.SamplePose(0, -.25f, pose);
        Near(new(1, -.5f, 0), pose[0]);
        mesh.SamplePose(-1, 0, pose);
        Assert.Equal(mesh.Vertices, pose);
        Assert.Equal(new Vector3(-.5f, -.5f, 0), mesh.Vertices[0]);
    }

    [Fact]
    public void NodeRotationUsesShortestQuaternionPathAndAnimatedParentHierarchy()
    {
        using var file = new GlbFixture();
        file.Root["nodes"] = JsonNode.Parse("""[{"translation":[4,0,0],"children":[1]},{"mesh":0,"translation":[0,2,0]}]""");
        var half = MathF.Sqrt(.5f);
        file.Clip("turn", 0, "rotation", [0, 1], [0, 0, 0, 1, 0, 0, -half, -half]);
        var mesh = file.Load(); var pose = new Vector3[3];
        mesh.SamplePose(0, 1, pose, false);
        Near(new(-2.5f, -2.5f, 0), pose[0]);
        mesh.SamplePose(0, .5f, pose);
        Near(new(half, half, 0), pose[1] - pose[0]);
    }

    [Fact]
    public void InPlaceRigidGuidePreservesFirstPoseOffsetAndVerticalMotionWithoutChangingDefaultSampling()
    {
        using var file = new GlbFixture();
        file.Root["nodes"]![0]!["translation"] = JsonNode.Parse("[4,2,3]");
        file.Clip("travel", 0, "translation", [0, 1], [6, 2, 8, 16, 5, 18]);
        var mesh = file.Load(); var start = new Vector3[3]; var free = new Vector3[3]; var pinned = new Vector3[3];
        mesh.SamplePose(0, 0, start); mesh.SamplePose(0, 1, free, loop: false);
        mesh.SamplePose(0, 1, pinned, loop: false, inPlace: true);
        for (var i = 0; i < pinned.Length; i++)
        {
            Near(start[i] + new Vector3(10, 3, 10), free[i]);
            Near(start[i] + Vector3.UnitY * 3, pinned[i]);
        }
        Near(mesh.Vertices[0] + new Vector3(2, 0, 5), start[0]); // First key's authored offset is retained.
        mesh.SamplePose(-1, 0, pinned, inPlace: true); Assert.Equal(mesh.Vertices, pinned);
    }

    [Fact]
    public void InPlaceSkinStripsLocomotionBoneWhilePreservingChildJointTranslationsAndIgnoringUnusedHelpers()
    {
        using var file = new GlbFixture();
        file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"skin":0},{"translation":[5,0,7],"children":[2,3]},{"translation":[0,1,0]},{"translation":[0,2,0]},{},{"children":[1,4]}]""");
        file.Root["scenes"] = JsonNode.Parse("""[{"nodes":[0,5]}]""");
        var inverses = new[] { Matrix4x4.CreateTranslation(-5, 0, -7), Matrix4x4.CreateTranslation(-5, -1, -7), Matrix4x4.CreateTranslation(-5, -2, -7), Matrix4x4.Identity };
        var bindId = file.Floats(inverses.SelectMany(MatrixValues).ToArray(), "MAT4");
        file.Root["skins"] = JsonSerializer.SerializeToNode(new[] { new { joints = new[] { 1, 2, 3, 4 }, inverseBindMatrices = bindId } });
        file.Attributes["JOINTS_0"] = file.Bytes([0, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0], "VEC4", false);
        file.Attributes["WEIGHTS_0"] = file.Floats([1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], "VEC4");
        file.Clip("walk", 1, "translation", [0, 1], [5, 0, 7, 15, 2, 17]);
        file.AddChannel(2, "translation", [0, 1], [0, 1, 0, 3, 1, 4]);
        var mesh = file.Load(); var pose = new Vector3[3];
        mesh.SamplePose(0, 1, pose, loop: false, inPlace: true);
        Near(mesh.Vertices[0] + new Vector3(0, 2, 0), pose[0]);
        Near(mesh.Vertices[1] + new Vector3(3, 2, 4), pose[1]);
        Near(mesh.Vertices[2] + new Vector3(0, 2, 0), pose[2]);
    }

    [Fact]
    public void InPlaceRigidHierarchyPreservesMotionOfSeparateChildPieces()
    {
        using var file = new GlbFixture();
        file.Root["nodes"] = JsonNode.Parse("""[{"translation":[4,0,0],"children":[1,2]},{"mesh":0},{"mesh":0,"translation":[0,2,0]}]""");
        file.Clip("move", 0, "translation", [0, 1], [4, 0, 0, 10, 0, 0]);
        file.AddChannel(2, "translation", [0, 1], [0, 2, 0, 3, 2, 0]);
        var mesh = file.Load(); var pose = new Vector3[6];
        mesh.SamplePose(0, 1, pose, loop: false, inPlace: true);
        Near(mesh.Vertices[0], pose[0]);
        Near(mesh.Vertices[3] + Vector3.UnitX, pose[3]); // Bind extent=3; child translates three units.
    }

    [Fact]
    public void InPlaceMotionUsesWorldHorizontalAxesUnderRotatedAndScaledParents()
    {
        using var file = new GlbFixture(); var half = MathF.Sqrt(.5f);
        file.Root["nodes"] = JsonSerializer.SerializeToNode(new object[]
        {
            new { rotation = new[] { half, 0f, 0f, half }, scale = new[] {2f,2f,2f}, children = new[] {1} },
            new { mesh = 0, translation = new[] {3f,4f,5f} }
        });
        file.Clip("travel", 1, "translation", [0, 1], [3, 4, 5, 4, 6, 8]);
        var mesh = file.Load(); var pose = new Vector3[3]; var start = new Vector3[3];
        mesh.SamplePose(0, 0, start); mesh.SamplePose(0, 1, pose, loop: false, inPlace: true);
        // Parent rotates local +Y into world +Z and local +Z into world -Y. Scale cancels bind normalization.
        for (var i = 0; i < pose.Length; i++) Near(start[i] + new Vector3(0, -3, 0), pose[i]);
    }

    [Fact]
    public void InPlaceSeparateSceneRootsRetainTheirAuthoredSeparation()
    {
        using var file = new GlbFixture();
        file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0},{"mesh":0,"translation":[3,0,0]}]""");
        file.Root["scenes"] = JsonNode.Parse("""[{"nodes":[0,1]}]""");
        file.Clip("travel", 0, "translation", [0, 1], [0, 0, 0, 10, 0, 0]);
        file.AddChannel(1, "translation", [0, 1], [3, 0, 0, 23, 0, 0]);
        var mesh = file.Load(); var pose = new Vector3[6];
        mesh.SamplePose(0, 1, pose, loop: false, inPlace: true);
        Assert.Equal(mesh.Vertices, pose);
    }

    [Fact]
    public void SkinUsesJointWorldTransformsInverseBindsAndNormalizedByteWeights()
    {
        using var file = new GlbFixture();
        file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"skin":0,"translation":[100,0,0]},{"translation":[2,0,0]},{"translation":[4,0,0]}]""");
        file.Root["scenes"] = JsonNode.Parse("""[{"nodes":[0,1,2]}]""");
        var bind = Matrix4x4.CreateTranslation(-2, 0, 0); var bind2 = Matrix4x4.CreateTranslation(-4, 0, 0);
        var bindId = file.Floats(MatrixValues(bind).Concat(MatrixValues(bind2)).ToArray(), "MAT4");
        file.Root["skins"] = JsonSerializer.SerializeToNode(new[] { new { joints = new[] { 1, 2 }, inverseBindMatrices = bindId } });
        file.Attributes["JOINTS_0"] = file.Bytes([0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0], "VEC4", false);
        file.Attributes["WEIGHTS_0"] = file.Bytes([127, 128, 0, 0, 127, 128, 0, 0, 127, 128, 0, 0], "VEC4", true);
        file.Clip("lift", 2, "translation", [0, 1], [4, 0, 0, 4, 2, 0]);
        var mesh = file.Load(); var pose = new Vector3[3];
        Near(new(-.5f, -.5f, 0), mesh.Vertices[0]); // The skinned mesh node's +100 translation is intentionally ignored per glTF.
        mesh.SamplePose(0, 1, pose, false);
        Near(new(-.5f, -.5f + 2 * 128f / 255, 0), pose[0]);
    }

    [Fact]
    public void SkinsWithoutInverseBindMatricesUseIdentityAndEveryInfluenceSet()
    {
        using var file = new GlbFixture();
        file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"skin":0},{},{}]""");
        file.Root["scenes"] = JsonNode.Parse("""[{"nodes":[0,1,2]}]""");
        file.Root["skins"] = JsonNode.Parse("""[{"joints":[1,2]}]""");
        file.Attributes["JOINTS_0"] = file.Bytes(new byte[12], "VEC4", false);
        file.Attributes["WEIGHTS_0"] = file.Floats([.25f, 0, 0, 0, .25f, 0, 0, 0, .25f, 0, 0, 0], "VEC4");
        file.Attributes["JOINTS_1"] = file.Bytes([1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], "VEC4", false);
        file.Attributes["WEIGHTS_1"] = file.Floats([.75f, 0, 0, 0, .75f, 0, 0, 0, .75f, 0, 0, 0], "VEC4");
        file.Clip("second influence set", 2, "translation", [0, 1], [0, 0, 0, 0, 2, 0]);
        var mesh = file.Load(); var pose = new Vector3[3];
        mesh.SamplePose(0, 1, pose, false);
        Near(new(-.5f, 1, 0), pose[0]);
    }

    [Fact]
    public void MorphWeightsDeformBeforeNodeTransformAndSupportSparseTargets()
    {
        using var file = new GlbFixture();
        var sparseIndex = file.Buffer([2]);
        var targetValues = file.FloatBuffer([0, 2, 0]);
        var targetId = file.Accessor(new JsonObject
        {
            ["componentType"] = 5126,
            ["count"] = 3,
            ["type"] = "VEC3",
            ["sparse"] = new JsonObject { ["count"] = 1, ["indices"] = new JsonObject { ["bufferView"] = sparseIndex, ["componentType"] = 5121 }, ["values"] = new JsonObject { ["bufferView"] = targetValues } }
        });
        file.Primitive["targets"] = JsonSerializer.SerializeToNode(new[] { new { POSITION = targetId } });
        file.Root["nodes"]![0]!["scale"] = JsonSerializer.SerializeToNode(new[] { 1, 2, 1 });
        file.Clip("bounce", 0, "weights", [0, 1], [0, 1]);
        var mesh = file.Load(); var pose = new Vector3[3];
        mesh.SamplePose(0, .5f, pose);
        Near(new(-.25f, 1.5f, 0), pose[2]); // Bind extent is 2; delta 2 * weight .5 * node scale 2 / extent 2 = +1.
    }

    [Fact]
    public void CubicSplineScalesTangentsByKeyIntervalAndStepHoldsUntilNextKey()
    {
        using var cubic = new GlbFixture();
        cubic.Clip("cubic", 0, "translation", [0, 2],
            [0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0], "CUBICSPLINE");
        var mesh = cubic.Load(); var pose = new Vector3[3]; mesh.SamplePose(0, 1, pose);
        Near(new(1, -.5f, 0), pose[0]); // Translation=1.5, not 1.25: outgoing tangent is scaled by two seconds.
        using var step = new GlbFixture();
        step.Clip("step", 0, "translation", [0, 1], [0, 0, 0, 2, 0, 0], "STEP");
        mesh = step.Load(); mesh.SamplePose(0, .99f, pose);
        Near(new(-.5f, -.5f, 0), pose[0]);
        mesh.SamplePose(0, 1, pose, false); Near(new(1.5f, -.5f, 0), pose[0]);
    }

    [Fact]
    public void CubicRotationIsNormalizedAndLateStartingChannelsClampToTheirFirstKey()
    {
        using var file = new GlbFixture(); var half = MathF.Sqrt(.5f);
        file.Clip("cubic turn", 0, "rotation", [1, 3],
            [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, half, half, 0, 0, 0, 0], "CUBICSPLINE");
        var mesh = file.Load(); var pose = new Vector3[3];
        mesh.SamplePose(0, 0, pose, false); Assert.Equal(mesh.Vertices, pose);
        mesh.SamplePose(0, 2, pose, false);
        Near(new(half, half, 0), pose[1] - pose[0]);
        Assert.InRange(Vector3.Distance(pose[1], pose[0]), .99999f, 1.00001f);
    }

    [Fact]
    public void NodeMorphWeightsOverrideMeshDefaultsInTheBindPose()
    {
        using var file = new GlbFixture(); var offsets = file.Floats([0, 0, 0, 0, 0, 0, 0, 2, 0], "VEC3");
        file.Primitive["targets"] = JsonSerializer.SerializeToNode(new[] { new { POSITION = offsets } });
        file.Root["meshes"]![0]!["weights"] = JsonNode.Parse("[0.25]");
        file.Root["nodes"]![0]!["weights"] = JsonNode.Parse("[0.5]");
        var mesh = file.Load();
        Near(new(-.25f, .5f, 0), mesh.Vertices[2]);
        Near(new(.25f, -.5f, 0), mesh.Vertices[1]);
    }

    [Fact]
    public void FiniteKeysThatOverflowAnimatedTransformsAreRejectedBeforeGpuSubmission()
    {
        using var file = new GlbFixture();
        file.Root["nodes"] = JsonNode.Parse("""[{"children":[1]},{"mesh":0,"translation":[2,0,0]}]""");
        file.Clip("invalid scale", 0, "scale", [0, 1], [1, 1, 1, float.MaxValue, float.MaxValue, float.MaxValue]);
        var mesh = file.Load(); var pose = new Vector3[3];
        Assert.Throws<InvalidDataException>(() => mesh.SamplePose(0, 1, pose, false));
    }

    [Fact]
    public void EmbeddedTextureMaterialUvTransformAndVertexColorsSurviveImport()
    {
        using var file = new GlbFixture();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var view = file.Buffer(png);
        file.Root["images"] = JsonSerializer.SerializeToNode(new[] { new { bufferView = view, mimeType = "image/png" } });
        file.Root["textures"] = JsonNode.Parse("""[{"source":0,"sampler":0}]""");
        file.Root["samplers"] = JsonNode.Parse("""[{"wrapS":33071,"wrapT":33648}]""");
        file.Root["materials"] = JsonNode.Parse("""[{"pbrMetallicRoughness":{"baseColorFactor":[0.5,0.6,0.7,0.8],"baseColorTexture":{"index":0,"extensions":{"KHR_texture_transform":{"offset":[0.1,0.2],"scale":[2,3],"texCoord":1}}}},"doubleSided":true,"alphaMode":"MASK","alphaCutoff":0.3,"extensions":{"KHR_materials_unlit":{}}}]""");
        file.Primitive["material"] = 0;
        file.Attributes["TEXCOORD_1"] = file.Floats([0, 0, 1, 0, 0, 1], "VEC2");
        file.Attributes["COLOR_0"] = file.Bytes([255, 0, 128, 255, 255, 255, 0, 255, 0], "VEC3", true);
        var mesh = file.Load(); var image = Assert.Single(mesh.Images); var material = mesh.Materials[0];
        Assert.Equal(png, image.Data); Assert.Equal((1, 1), (image.Width, image.Height));
        Assert.Equal(new Vector2(2.1f, .2f), mesh.TextureCoordinates[1]);
        Assert.Equal(new Vector4(1, 0, 128f / 255, 1), mesh.VertexColors[0]);
        Assert.Equal(new Vector4(.5f, .6f, .7f, .8f), Assert.Single(mesh.FaceColors));
        Assert.Equal(0, Assert.Single(mesh.FaceMaterials)); Assert.Equal(0, material.BaseColorImage);
        Assert.True(material.DoubleSided); Assert.True(material.Unlit); Assert.Equal("MASK", material.AlphaMode);
        Assert.Equal(.3f, material.AlphaCutoff); Assert.Equal(33071, material.WrapU); Assert.Equal(33648, material.WrapV);
    }

    [Theory]
    [InlineData("time-order")]
    [InlineData("nan-key")]
    [InlineData("bad-channel-size")]
    [InlineData("matrix-animation")]
    [InlineData("bad-joint")]
    [InlineData("zero-weights")]
    [InlineData("cycle")]
    [InlineData("deep-hierarchy")]
    [InlineData("oversized-texture")]
    [InlineData("external-texture")]
    [InlineData("required-compression")]
    [InlineData("accessor-spill")]
    public void InvalidAndExcessiveInputsAreRejectedBeforeRendering(string variant)
    {
        using var file = new GlbFixture();
        switch (variant)
        {
            case "time-order": file.Clip("invalid", 0, "translation", [1, 0], [0, 0, 0, 1, 0, 0]); break;
            case "nan-key": file.Clip("invalid", 0, "translation", [0, 1], [0, 0, 0, float.NaN, 0, 0]); break;
            case "bad-channel-size": file.Clip("invalid", 0, "translation", [0, 1], [0, 0, 0]); break;
            case "matrix-animation":
                file.Root["nodes"]![0]!["matrix"] = JsonSerializer.SerializeToNode(MatrixValues(Matrix4x4.Identity));
                file.Clip("invalid", 0, "translation", [0, 1], [0, 0, 0, 1, 0, 0]); break;
            case "bad-joint":
            case "zero-weights":
                file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"skin":0},{}]"""); file.Root["skins"] = JsonNode.Parse("""[{"joints":[1]}]""");
                file.Attributes["JOINTS_0"] = file.Bytes(Enumerable.Repeat((byte)(variant == "bad-joint" ? 1 : 0), 12).ToArray(), "VEC4", false);
                file.Attributes["WEIGHTS_0"] = file.Floats(Enumerable.Repeat(variant == "bad-joint" ? .25f : 0, 12).ToArray(), "VEC4"); break;
            case "cycle": file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"children":[1]},{"children":[0]}]"""); break;
            case "deep-hierarchy":
                var nodes = new JsonArray(); for (var i = 0; i < 66; i++) nodes.Add(i < 65 ? new JsonObject { ["children"] = new JsonArray(i + 1) } : new JsonObject { ["mesh"] = 0 });
                file.Root["nodes"] = nodes; break;
            case "oversized-texture":
                var png = new byte[33]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
                BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13); "IHDR"u8.CopyTo(png.AsSpan(12));
                BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 100000); BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 100000);
                var view = file.Buffer(png); file.Root["images"] = JsonSerializer.SerializeToNode(new[] { new { bufferView = view, mimeType = "image/png" } }); break;
            case "external-texture": file.Root["images"] = JsonNode.Parse("""[{"uri":"../../private.png"}]"""); break;
            case "required-compression": file.Root["extensionsRequired"] = JsonNode.Parse("""["KHR_draco_mesh_compression"]"""); break;
            case "accessor-spill": file.Root["accessors"]![0]!["byteOffset"] = 4; break;
        }
        if (variant is "oversized-texture" or "external-texture")
        {
            file.Root["textures"] = JsonNode.Parse("""[{"source":0}]""");
            file.Root["materials"] = JsonNode.Parse("""[{"pbrMetallicRoughness":{"baseColorTexture":{"index":0}}}]""");
        }
        Assert.Throws<InvalidDataException>(() => file.Load());
    }

    [Fact]
    public void ImageDimensionsReadJpegSegmentsAndRejectTruncation()
    {
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xe0, 0, 4, 0, 0, 0xff, 0xc2, 0, 8, 8, 0, 20, 0, 30, 0];
        Assert.Equal((30, 20), GuideImageBounds.Read("image/jpeg", jpeg));
        Assert.Throws<InvalidDataException>(() => GuideImageBounds.Read("image/jpeg", jpeg[..^1]));
    }

    private static void Near(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < .0001f, $"Expected {expected}; received {actual}.");
    private static float[] MatrixValues(Matrix4x4 m) => [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    private sealed class GlbFixture : IDisposable
    {
        public JsonObject Root { get; } = JsonNode.Parse("""{"asset":{"version":"2.0"},"bufferViews":[],"accessors":[],"meshes":[{"primitives":[{"attributes":{}}]}],"nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}]}""")!.AsObject();
        public JsonObject Primitive => Root["meshes"]![0]!["primitives"]![0]!.AsObject();
        public JsonObject Attributes => Primitive["attributes"]!.AsObject();
        private readonly MemoryStream bin = new();
        private readonly string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".glb");
        public GlbFixture() { Attributes["POSITION"] = Floats([0, 0, 0, 1, 0, 0, 0, 1, 0], "VEC3"); }
        public int Buffer(byte[] data)
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            var views = Root["bufferViews"]!.AsArray(); var id = views.Count;
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = data.Length }); bin.Write(data); return id;
        }
        public int FloatBuffer(float[] data)
        {
            var bytes = new byte[data.Length * 4];
            for (var i = 0; i < data.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), data[i]);
            return Buffer(bytes);
        }
        public int Accessor(JsonObject value) { var accessors = Root["accessors"]!.AsArray(); var id = accessors.Count; accessors.Add(value); return id; }
        public int Floats(float[] values, string type) => Accessor(new JsonObject { ["bufferView"] = FloatBuffer(values), ["componentType"] = 5126, ["count"] = values.Length / Width(type), ["type"] = type });
        public int Bytes(byte[] values, string type, bool normalized) => Accessor(new JsonObject { ["bufferView"] = Buffer(values), ["componentType"] = 5121, ["count"] = values.Length / Width(type), ["type"] = type, ["normalized"] = normalized });
        public void Clip(string name, int node, string property, float[] times, float[] output, string interpolation = "LINEAR")
        {
            var input = Floats(times, "SCALAR"); var values = Floats(output, property == "rotation" ? "VEC4" : property == "weights" ? "SCALAR" : "VEC3");
            Root["animations"] = JsonSerializer.SerializeToNode(new[] { new { name, samplers = new[] { new { input, output = values, interpolation } }, channels = new[] { new { sampler = 0, target = new { node, path = property } } } } });
        }
        public void AddChannel(int node, string property, float[] times, float[] output, string interpolation = "LINEAR")
        {
            var input = Floats(times, "SCALAR"); var values = Floats(output, property == "rotation" ? "VEC4" : property == "weights" ? "SCALAR" : "VEC3");
            var animation = Root["animations"]![0]!; var samplers = animation["samplers"]!.AsArray(); var id = samplers.Count;
            samplers.Add(JsonSerializer.SerializeToNode(new { input, output = values, interpolation }));
            animation["channels"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { sampler = id, target = new { node, path = property } }));
        }
        public GuideMesh Load()
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            Root["buffers"] = JsonSerializer.SerializeToNode(new[] { new { byteLength = bin.Length } });
            var json = Encoding.UTF8.GetBytes(Root.ToJsonString()); var length = (json.Length + 3) / 4 * 4;
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(0x46546C67u); writer.Write(2u); writer.Write((uint)(12 + 8 + length + 8 + bin.Length));
                writer.Write(length); writer.Write(0x4e4f534au); writer.Write(json);
                for (var i = json.Length; i < length; i++) writer.Write((byte)32);
                writer.Write((int)bin.Length); writer.Write(0x004e4942u); writer.Write(bin.ToArray());
            }
            return GuideMesh.Load(path);
        }
        private static int Width(string type) => type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw new ArgumentException(type) };
        public void Dispose() { bin.Dispose(); File.Delete(path); }
    }
}
