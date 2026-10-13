using System.Numerics;
using System.Text.Json.Nodes;

namespace XivWayfinder.Core.Tests;

public sealed partial class GuideMeshAnimationTests
{
    [Theory]
    [InlineData(2, 1, 3)]
    [InlineData(-2, 1, 3)]
    [InlineData(-2, -1, -3)]
    public void AuthoredNormalsUseInverseTransposeForNonuniformAndMirroredNodes(float x, float y, float z)
    {
        using var file = new GlbFixture();
        SetNormals(file, Vector3.Normalize(Vector3.One));
        file.Root["nodes"]![0]!["scale"] = new JsonArray(x,y,z);
        file.Root["nodes"]![0]!["translation"] = new JsonArray(100,200,300);
        var mesh = file.Load();
        foreach (var normal in mesh.Normals) Near(Vector3.Normalize(new(1/x,1/y,1/z)), normal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RigidAndSkeletalNormalsFollowSampledRotationAndPoseCrossfades(bool skin)
    {
        using var file = new GlbFixture(); SetNormals(file, Vector3.UnitZ);
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
        var h = MathF.Sqrt(.5f);
        file.Clip("turn", node, "rotation", [0,1], [0,0,0,1, 0,h,0,h]);
        var mesh = file.Load(); var positions = new Vector3[3]; var normals = new Vector3[3];
        mesh.SamplePose(0, .5f, positions, normals: normals);
        foreach (var normal in normals) Near(new(h,0,h), normal);
        mesh.SampleBlendedPose(new(-1,0,0,1,.5f), positions, loop:false, normals:normals);
        foreach (var normal in normals) Near(new(h,0,h), normal);
        mesh.SamplePose(-1, 0, positions, normals:normals);
        Assert.Equal(mesh.Normals, normals);
    }

    [Fact]
    public void SkinnedNormalsUseInverseTransposeOfBlendedJointTransform()
    {
        using var file = new GlbFixture(); SetNormals(file, Vector3.Normalize(new(1,1,0)));
        file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"skin":0,"translation":[100,0,0]},{"scale":[2,1,1]},{}]""");
        file.Root["scenes"] = JsonNode.Parse("""[{"nodes":[0,1,2]}]""");
        file.Root["skins"] = JsonNode.Parse("""[{"joints":[1,2]}]""");
        file.Attributes["JOINTS_0"] = file.Bytes([0,1,0,0, 0,1,0,0, 0,1,0,0], "VEC4", false);
        file.Attributes["WEIGHTS_0"] = file.Floats([.5f,.5f,0,0, .5f,.5f,0,0, .5f,.5f,0,0], "VEC4");
        var mesh = file.Load();
        foreach (var normal in mesh.Normals) Near(Vector3.Normalize(new(1/1.5f,1,0)), normal);
    }

    [Fact]
    public void NormalMorphDeltasUseInterpolatedAndBlendedWeightsBeforeNodeTransform()
    {
        using var file = new GlbFixture(); SetNormals(file, Vector3.UnitZ);
        var target = file.Floats([0,1,-1, 0,1,-1, 0,1,-1], "VEC3");
        file.Primitive["targets"] = new JsonArray(new JsonObject { ["NORMAL"] = target });
        file.Root["nodes"]![0]!["scale"] = new JsonArray(1,2,1);
        file.Clip("lean", 0, "weights", [0,1], [0,1]);
        var mesh = file.Load(); var positions = new Vector3[3]; var normals = new Vector3[3];
        mesh.SamplePose(0,.5f,positions,normals:normals);
        foreach (var normal in normals) Near(Vector3.Normalize(new(0,.25f,.5f)), normal);
        mesh.SampleBlendedPose(new(-1,0,0,1,.5f),positions,loop:false,normals:normals);
        foreach (var normal in normals) Near(Vector3.Normalize(new(0,.25f,.5f)), normal);
        Assert.Equal(mesh.Vertices, positions);
    }

    [Fact]
    public void InverseBindMatrixAlsoTransformsSkinNormals()
    {
        using var file = new GlbFixture(); SetNormals(file, Vector3.Normalize(new(1,1,0)));
        var inverse = file.Floats(MatrixValues(Matrix4x4.CreateScale(2,1,1)), "MAT4");
        file.Root["nodes"] = JsonNode.Parse("""[{"mesh":0,"skin":0},{}]""");
        file.Root["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(1), ["inverseBindMatrices"] = inverse });
        file.Root["scenes"] = JsonNode.Parse("""[{"nodes":[0,1]}]""");
        file.Attributes["JOINTS_0"] = file.Bytes(new byte[12], "VEC4", false);
        file.Attributes["WEIGHTS_0"] = file.Floats([1,0,0,0, 1,0,0,0, 1,0,0,0], "VEC4");
        foreach (var normal in file.Load().Normals) Near(Vector3.Normalize(new(.5f,1,0)), normal);
    }

    [Fact]
    public void MissingAndZeroNormalsRequestGeometricFallbackWithoutKeepingOldFrameValues()
    {
        using var file = new GlbFixture(); file.Clip("move",0,"translation",[0,1],[0,0,0, 1,0,0]);
        var mesh = file.Load(); var positions = new Vector3[3]; var normals = Enumerable.Repeat(Vector3.One,3).ToArray();
        mesh.SamplePose(0,.5f,positions,normals:normals);
        Assert.All(normals, n => Assert.Equal(Vector3.Zero,n));
        SetNormals(file, Vector3.Zero);
        Assert.All(file.Load().Normals, n => Assert.Equal(Vector3.Zero,n));
    }

    [Fact]
    public void SingularAnimatedScaleUsesGeometricFallbackAndRestoresAuthoredNormalsLater()
    {
        using var file = new GlbFixture(); SetNormals(file, Vector3.UnitZ);
        file.Clip("flatten",0,"scale",[0,1],[1,1,1, 1,1,0]);
        var mesh = file.Load(); var positions = new Vector3[3]; var normals = new Vector3[3];
        mesh.SamplePose(0,1,positions,loop:false,normals:normals);
        Assert.All(normals,n=>Assert.Equal(Vector3.Zero,n));
        Assert.All(positions,n=>Assert.True(float.IsFinite(n.X+n.Y+n.Z)));
        mesh.SamplePose(0,0,positions,normals:normals);
        Assert.All(normals,n=>Assert.Equal(Vector3.UnitZ,n));
    }

    [Fact]
    public void NormalBufferValidationAndStaticBindSamplingAreConsistent()
    {
        using var file = new GlbFixture(); SetNormals(file, Vector3.UnitZ);
        var mesh = file.Load();
        Assert.Throws<ArgumentException>(()=>mesh.SamplePose(-1,0,new Vector3[3],normals:new Vector3[2]));
        Assert.Throws<ArgumentException>(()=>mesh.SampleBlendedPose(new(-1,0,-1,0,.5f),new Vector3[3],normals:new Vector3[2]));
        var normals = new Vector3[3]; mesh.SampleBlendedPose(new(-1,0,-1,0,.5f),new Vector3[3],normals:normals);
        Assert.Equal(mesh.Normals,normals);
    }

    [Fact]
    public void InvalidNormalAccessorsAndMissingBaseMorphNormalsAreRejected()
    {
        using var file = new GlbFixture();
        file.Attributes["NORMAL"] = file.Floats([0,0,1],"VEC3");
        Assert.Throws<InvalidDataException>(()=>file.Load());
        file.Attributes.Remove("NORMAL");
        file.Primitive["targets"] = new JsonArray(new JsonObject { ["NORMAL"] = file.Floats([0,1,0, 0,1,0, 0,1,0],"VEC3") });
        Assert.Throws<InvalidDataException>(()=>file.Load());
    }

    [Fact]
    public void NormalTransformHandlesShearAndExtremeUniformScalesWithoutNaN()
    {
        var shear = Matrix4x4.Identity; shear.M12 = 2;
        Near(Vector3.Normalize(new(-2,1,0)),new GuideNormalTransform(shear).Apply(Vector3.UnitY));
        foreach (var magnitude in new[] { 1e-30f,1e30f,-1e30f })
            Near(Vector3.UnitZ*MathF.Sign(magnitude),new GuideNormalTransform(Matrix4x4.CreateScale(magnitude)).Apply(Vector3.UnitZ));
        Assert.Equal(Vector3.Zero,new GuideNormalTransform(Matrix4x4.CreateScale(0)).Apply(Vector3.UnitZ));
    }

    private static void SetNormals(GlbFixture file, Vector3 normal) => file.Attributes["NORMAL"] = file.Floats(
        [normal.X,normal.Y,normal.Z, normal.X,normal.Y,normal.Z, normal.X,normal.Y,normal.Z],"VEC3");
}
