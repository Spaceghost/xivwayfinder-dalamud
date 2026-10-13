using System.Numerics;
using System.Text.Json.Nodes;

namespace XivWayfinder.Core.Tests;

public sealed partial class GuideMeshAnimationTests
{
    [Fact]
    public void UnreferencedAccessorVerticesCannotShrinkOrRaiseTheVisibleModel()
    {
        using var file = new GlbFixture();
        file.Attributes["POSITION"] = file.Floats([0,0,0, 1,0,0, 0,1,0, 1000,-1000,1000], "VEC3");
        file.Primitive["indices"] = file.Bytes([0,1,2], "SCALAR", false);
        var mesh = file.Load();
        Assert.Equal(3, mesh.Vertices.Length);
        Near(new(-.5f,-.5f,0), mesh.Vertices[0]);
        Near(new(.5f,-.5f,0), mesh.Vertices[1]);
        Near(new(-.5f,.5f,0), mesh.Vertices[2]);
    }

    [Fact]
    public void RetainedVertexLimitCountsTheIndexedGeometryInsteadOfUnusedAccessorCapacity()
    {
        using var file = new GlbFixture();
        var positions = new float[60_001 * 3]; positions[3] = 1; positions[7] = 1;
        file.Attributes["POSITION"] = file.Floats(positions, "VEC3");
        file.Primitive["indices"] = file.Bytes([0,1,2], "SCALAR", false);
        Assert.Equal(3, file.Load().Vertices.Length);
    }

    [Fact]
    public void CompactIndexedAttributesAndMorphsKeepTheirOriginalVertexCorrespondence()
    {
        using var file = new GlbFixture();
        file.Attributes["POSITION"] = file.Floats([100,100,100, 0,0,0, -100,-100,-100, 2,0,0, 0,2,0], "VEC3");
        file.Attributes["NORMAL"] = file.Floats([1,0,0, 0,0,1, 1,0,0, 0,1,0, 1,0,0], "VEC3");
        file.Attributes["TEXCOORD_0"] = file.Floats([9,9, .1f,.2f, 8,8, .3f,.4f, .5f,.6f], "VEC2");
        file.Attributes["COLOR_0"] = file.Floats([0,0,0,1, 1,0,0,1, 0,0,0,1, 0,1,0,1, 0,0,1,1], "VEC4");
        file.Primitive["indices"] = file.Bytes([4,1,3], "SCALAR", false);
        var target = file.Floats([100,100,100, 0,0,2, 100,100,100, 0,2,0, 2,0,0], "VEC3");
        var normalTarget = file.Floats([0,0,0, 0,1,-1, 0,0,0, 1,-1,0, -1,0,1], "VEC3");
        file.Primitive["targets"] = new JsonArray(new JsonObject { ["POSITION"] = target, ["NORMAL"] = normalTarget });
        file.Clip("morph", 0, "weights", [0,1], [0,1]);
        var mesh = file.Load(); var pose = new Vector3[3]; var normals = new Vector3[3];
        Assert.Equal(new[] {2,0,1}, mesh.Indices);
        Assert.Equal(new[] { new Vector2(.1f,.2f),new Vector2(.3f,.4f),new Vector2(.5f,.6f) }, mesh.TextureCoordinates);
        Assert.Equal(new[] { new Vector4(1,0,0,1),new Vector4(0,1,0,1),new Vector4(0,0,1,1) }, mesh.VertexColors);
        mesh.SamplePose(0, 1, pose, loop:false, normals:normals);
        Near(mesh.Vertices[0] + Vector3.UnitZ, pose[0]);
        Near(mesh.Vertices[1] + Vector3.UnitY, pose[1]);
        Near(mesh.Vertices[2] + Vector3.UnitX, pose[2]);
        Assert.Equal(new[] {Vector3.UnitY,Vector3.UnitX,Vector3.UnitZ}, normals);
    }

    [Fact]
    public void UnusedSkinInfluencesCannotPreventRemovingTheVisibleLocomotionRoot()
    {
        using var file = new GlbFixture();
        file.Attributes["POSITION"] = file.Floats([0,0,0, 1,0,0, 0,1,0, 1000,-1000,1000], "VEC3");
        file.Primitive["indices"] = file.Bytes([0,1,2], "SCALAR", false);
        file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"skin":0},{},{},{"children":[1,2]}]""");
        file.Root["scenes"] = JsonNode.Parse("""[{"nodes":[0,3]}]""");
        file.Root["skins"] = JsonNode.Parse("""[{"joints":[1,2]}]""");
        file.Attributes["JOINTS_0"] = file.Bytes([0,0,0,0, 0,0,0,0, 0,0,0,0, 1,0,0,0], "VEC4", false);
        file.Attributes["WEIGHTS_0"] = file.Floats([1,0,0,0, 1,0,0,0, 1,0,0,0, 1,0,0,0], "VEC4");
        file.Clip("walk", 1, "translation", [0,1], [0,0,0, 5,2,0]);
        var mesh = file.Load(); var pose = new Vector3[3];
        mesh.SamplePose(0, 1, pose, loop:false, inPlace:true);
        for (var i = 0; i < pose.Length; i++) Near(mesh.Vertices[i] + Vector3.UnitY * 2, pose[i]);
        mesh.SamplePose(0, 1, pose, loop:false, inPlace:false);
        for (var i = 0; i < pose.Length; i++) Near(mesh.Vertices[i] + new Vector3(5,2,0), pose[i]);
    }

    [Fact]
    public void SeparatePrimitivesSharingAttributesKeepTheirOwnRemappedIndicesAndMaterials()
    {
        using var file = new GlbFixture();
        file.Attributes["POSITION"] = file.Floats([0,0,0, 1,0,0, 0,1,0, 1,1,0], "VEC3");
        file.Primitive["indices"] = file.Bytes([0,1,2], "SCALAR", false);
        file.Primitive["material"] = 0;
        file.Root["materials"] = JsonNode.Parse("""[{"pbrMetallicRoughness":{"baseColorFactor":[1,0,0,1]}},{"pbrMetallicRoughness":{"baseColorFactor":[0,1,0,1]}}]""");
        var second = file.Primitive.DeepClone();
        second["indices"] = file.Bytes([3,2,1], "SCALAR", false); second["material"] = 1;
        file.Root["meshes"]![0]!["primitives"]!.AsArray().Add(second);
        var mesh = file.Load();
        Assert.Equal(6, mesh.Vertices.Length);
        Assert.Equal(new[] {0,1,2,5,4,3}, mesh.Indices);
        Assert.Equal(new[] {0,1}, mesh.FaceMaterials);
        Assert.Equal(new[] {new Vector4(1,0,0,1),new Vector4(0,1,0,1)}, mesh.FaceColors);
        Near(mesh.Vertices[1], mesh.Vertices[3]); Near(mesh.Vertices[2], mesh.Vertices[4]);
    }

    [Fact]
    public void DiscardedDegeneratePrimitivesDoNotAffectBoundsOrLaterBindingOffsets()
    {
        using var file = new GlbFixture();
        file.Attributes["POSITION"] = file.Floats([1000,-1000,1000], "VEC3");
        file.Primitive["indices"] = file.Bytes([0,0,0], "SCALAR", false);
        var second = file.Primitive.DeepClone();
        second["attributes"]!["POSITION"] = file.Floats([0,0,0, 1,0,0, 0,1,0], "VEC3");
        second["indices"] = file.Bytes([0,1,2], "SCALAR", false);
        file.Root["meshes"]![0]!["primitives"]!.AsArray().Add(second);
        file.Clip("move", 0, "translation", [0,1], [0,0,0, 1,0,0]);
        var mesh = file.Load(); var pose = new Vector3[3];
        Assert.Equal(3, mesh.Vertices.Length); Assert.Equal(new[] {0,1,2}, mesh.Indices);
        mesh.SamplePose(0,1,pose,loop:false);
        for (var i = 0; i < pose.Length; i++) Near(mesh.Vertices[i] + Vector3.UnitX, pose[i]);
    }

    [Fact]
    public void SkinUsesItsJointPrefixOfALongerInverseBindAccessor()
    {
        using var file = new GlbFixture();
        var inverse = file.Floats(MatrixValues(Matrix4x4.CreateTranslation(-2,0,0))
            .Concat(MatrixValues(Matrix4x4.CreateScale(500))).ToArray(), "MAT4");
        file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"skin":0},{"translation":[2,0,0]}]""");
        file.Root["scenes"] = JsonNode.Parse("""[{"nodes":[0,1]}]""");
        file.Root["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(1), ["inverseBindMatrices"] = inverse });
        file.Attributes["JOINTS_0"] = file.Bytes(new byte[12], "VEC4", false);
        file.Attributes["WEIGHTS_0"] = file.Floats([1,0,0,0, 1,0,0,0, 1,0,0,0], "VEC4");
        file.Clip("move", 1, "translation", [0,1], [2,0,0, 3,0,0]);
        var mesh = file.Load(); var pose = new Vector3[3];
        Near(new(-.5f,-.5f,0), mesh.Vertices[0]);
        mesh.SamplePose(0,1,pose,loop:false);
        for (var i = 0; i < pose.Length; i++) Near(mesh.Vertices[i] + Vector3.UnitX, pose[i]);
    }

    [Fact]
    public void InverseBindAccessorStillNeedsAMatrixForEveryJoint()
    {
        using var file = new GlbFixture();
        var inverse = file.Floats(MatrixValues(Matrix4x4.Identity), "MAT4");
        file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"skin":0},{"children":[2]},{}]""");
        file.Root["scenes"] = JsonNode.Parse("""[{"nodes":[0,1]}]""");
        file.Root["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(1,2), ["inverseBindMatrices"] = inverse });
        file.Attributes["JOINTS_0"] = file.Bytes(new byte[12], "VEC4", false);
        file.Attributes["WEIGHTS_0"] = file.Floats([1,0,0,0, 1,0,0,0, 1,0,0,0], "VEC4");
        Assert.Contains("Invalid inverse bind matrices", Assert.Throws<InvalidDataException>(() => file.Load()).Message);
    }
}
