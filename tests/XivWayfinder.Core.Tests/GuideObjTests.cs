using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuideObjTests
{
    [Fact]
    public void ObjAuthoredNormalSeamsAndNegativeNormalIndicesArePreserved()
    {
        using var fixture = new Fixture();
        fixture.Write("guide.obj", "v 0 0 0\nv 1 0 0\nv 0 1 0\nv 0 0 1\nvn 0 0 2\nvn 0 3 0\nf 1//1 2//1 3//1\nf 1//-1 4//-1 2//-1\n");
        var mesh = fixture.Load();
        Assert.Equal(6, mesh.Vertices.Length);
        Assert.Equal(mesh.Vertices[0], mesh.Vertices[3]);
        Assert.Equal(Vector3.UnitZ, mesh.Normals[0]); Assert.Equal(Vector3.UnitY, mesh.Normals[3]);
        var normals = new Vector3[6]; mesh.SamplePose(-1, 0, new Vector3[6], normals: normals);
        Assert.Equal(mesh.Normals, normals);
    }

    [Fact]
    public void ObjSmoothingGroupsJoinUvSeamsButKeepSeparateGroupsAndFlatFaces()
    {
        using var fixture = new Fixture();
        fixture.Write("guide.obj", "v 0 0 0\nv 1 0 0\nv 0 1 0\nv 0 0 1\nvt 0 0\nvt 1 1\ns 1\nf 1/1 2/1 3/1\nf 1/2 4/2 2/2\ns 2\nf 1 2 3\ns off\nf 1 4 2\n");
        var mesh = fixture.Load();
        var expected = Vector3.Normalize(new Vector3(0, 1, 1));
        Assert.Equal(expected, mesh.Normals[mesh.Indices[0]]);
        Assert.Equal(expected, mesh.Normals[mesh.Indices[3]]);
        Assert.Equal(Vector3.UnitZ, mesh.Normals[mesh.Indices[6]]);
        Assert.Equal(Vector3.Zero, mesh.Normals[mesh.Indices[9]]);
    }

    [Fact]
    public void ObjAuthoredNormalsTakePrecedenceOverSmoothingGroupGeneration()
    {
        using var fixture = new Fixture();
        fixture.Write("guide.obj", "v 0 0 0\nv 1 0 0\nv 0 1 0\nvn 0 1 0\ns 1\nf 1//1 2//1 3//1\n");
        Assert.All(fixture.Load().Normals, n => Assert.Equal(Vector3.UnitY, n));
    }

    [Fact]
    public void ObjMissingNormalsUseAllAdjacentSameGroupFacesWhileExplicitNormalsRemainAuthored()
    {
        using var fixture = new Fixture();
        fixture.Write("guide.obj", "v 0 0 0\nv 1 0 0\nv 0 1 0\nv 0 0 1\nvn 0 0 1\ns 1\nf 1//1 2//1 3//1\nf 1 4 2\n");
        var mesh = fixture.Load();
        Assert.Equal(Vector3.UnitZ, mesh.Normals[mesh.Indices[0]]);
        Assert.Equal(Vector3.Normalize(new Vector3(0, 1, 1)), mesh.Normals[mesh.Indices[3]]);
        Assert.Equal(Vector3.Normalize(new Vector3(0, 1, 1)), mesh.Normals[mesh.Indices[5]]);
        Assert.Equal(Vector3.UnitY, mesh.Normals[mesh.Indices[4]]);
    }

    [Fact]
    public void ObjRejectsMissingNormalReferences()
    {
        using var fixture = new Fixture();
        fixture.Write("guide.obj", "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1//1 2//1 3//1\n");
        Assert.Throws<InvalidDataException>(() => fixture.Load());
    }

    [Fact]
    public void ObjLoadsDiffuseMaterialOpacityTextureTransformAndVertexColor()
    {
        using var fixture = new Fixture();
        fixture.Write("guide.mtl", "newmtl blue material\nKd 0.2 0.4 0.8\nd 0.6\nillum 0\nmap_Kd -s 2 3 1 -o 0.1 0.2 0 -clamp on \"guide image.png\"\n");
        fixture.Png("guide image.png");
        fixture.Write("guide.obj", "mtllib guide.mtl\nv 0 0 0 1 0 0\nv 1 0 0 0 1 0\nv 0 1 0 0 0 1\nvt 0 0\nvt 1 0\nvt 0 1\nusemtl blue material\nf 1/1 2/2 3/3\n");
        var mesh = fixture.Load(); var material = Assert.Single(mesh.Materials);
        Assert.Equal(new Vector4(.2f, .4f, .8f, .6f), Assert.Single(mesh.FaceColors));
        Assert.Equal(new Vector4(1, 0, 0, 1), mesh.VertexColors[0]);
        Assert.Equal(new Vector2(.1f, .8f), mesh.TextureCoordinates[0]);
        Assert.InRange(mesh.TextureCoordinates[2].Y, -2.2001f, -2.1999f);
        Assert.Equal(0, material.BaseColorImage); Assert.Equal("BLEND", material.AlphaMode);
        Assert.True(material.Unlit); Assert.Equal(33071, material.WrapU); Assert.Equal(33071, material.WrapV);
        var image = Assert.Single(mesh.Images); Assert.Equal((1, 1), (image.Width, image.Height));
    }

    [Fact]
    public void ObjUvSeamsAndDifferentMaterialsDoNotShareIncompatibleVertices()
    {
        using var fixture = new Fixture();
        fixture.Write("guide.mtl", "newmtl red\nKd 1 0 0\nnewmtl green\nKd 0 1 0\nTr 0.2\n");
        fixture.Write("guide.obj", "mtllib guide.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nusemtl red\nf -3/-3 -2/-2 -1/-1\nusemtl green\nf 1/3 3/2 2/1\n");
        var mesh = fixture.Load();
        Assert.Equal(6, mesh.Vertices.Length); Assert.Equal(mesh.Vertices[0], mesh.Vertices[3]);
        Assert.NotEqual(mesh.TextureCoordinates[0], mesh.TextureCoordinates[3]);
        Assert.Equal(new Vector4(1, 0, 0, 1), mesh.FaceColors[0]);
        Assert.Equal(new Vector4(0, 1, 0, .8f), mesh.FaceColors[1]);
    }

    [Fact]
    public void RelativeTexturePathsResolveFromTheMtlFolderAndImagesAreDeduplicated()
    {
        using var fixture = new Fixture(); fixture.Png("materials/image.png");
        fixture.Write("materials/guide.mtl", "newmtl first\nmap_Kd image.png\nnewmtl second\nmap_Kd image.png\n");
        fixture.Write("guide.obj", "mtllib materials/guide.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nusemtl first\nf 1/1 2/1 3/1\nusemtl second\nf 1/1 2/1 3/1\n");
        var mesh = fixture.Load(); Assert.Single(mesh.Images);
        Assert.Equal(0, mesh.Materials[0].BaseColorImage); Assert.Equal(0, mesh.Materials[1].BaseColorImage);
    }

    [Theory]
    [InlineData("../outside.mtl")]
    [InlineData("/tmp/outside.mtl")]
    public void ObjRejectsMaterialReferencesOutsideSelectedFolder(string path)
    {
        using var fixture = new Fixture(); fixture.Write("guide.obj", "mtllib " + path + "\n");
        Assert.Throws<InvalidDataException>(() => fixture.Load());
    }

    [Fact]
    public void ObjRejectsTextureReferencesAndSymlinksOutsideSelectedFolder()
    {
        using var fixture = new Fixture(); using var other = new Fixture(); other.Png("outside.png");
        fixture.Write("guide.obj", "mtllib guide.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nusemtl first\nf 1/1 2/1 3/1\n");
        fixture.Write("guide.mtl", "newmtl first\nmap_Kd ../outside.png\n");
        Assert.Throws<InvalidDataException>(() => fixture.Load());
        File.CreateSymbolicLink(Path.Combine(fixture.Directory, "linked.png"), Path.Combine(other.Directory, "outside.png"));
        fixture.Write("guide.mtl", "newmtl first\nmap_Kd linked.png\n");
        Assert.Throws<InvalidDataException>(() => fixture.Load());
    }

    [Fact]
    public void ObjAllowsInternalSymlinksAndCountsTotalSidecarBytesBeforeReadingImages()
    {
        using var fixture = new Fixture(); fixture.Png("image.png");
        File.CreateSymbolicLink(Path.Combine(fixture.Directory, "linked.png"), Path.Combine(fixture.Directory, "image.png"));
        fixture.Write("guide.obj", "mtllib guide.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nusemtl first\nf 1/1 2/1 3/1\n");
        fixture.Write("guide.mtl", "newmtl first\nmap_Kd linked.png\n");
        Assert.Single(fixture.Load().Images);
        using (var stream = File.OpenWrite(Path.Combine(fixture.Directory, "image.png"))) stream.SetLength(33 * 1024 * 1024);
        Assert.Contains("32 MB", Assert.Throws<InvalidDataException>(() => fixture.Load()).Message);
    }

    [Theory]
    [InlineData("f 1/1 2/1 3/1", "")]
    [InlineData("f 1/1 2/1 3/1", "vt nan 1\n")]
    [InlineData("usemtl missing\nf 1 2 3", "")]
    public void InvalidTextureCoordinatesOrMissingMaterialsProduceAnImportError(string faces, string extra)
    {
        using var fixture = new Fixture(); fixture.Write("guide.obj", "v 0 0 0\nv 1 0 0\nv 0 1 0\n" + extra + faces);
        Assert.Throws<InvalidDataException>(() => fixture.Load());
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "wayfinder-obj-" + Guid.NewGuid());
        public Fixture() { System.IO.Directory.CreateDirectory(Directory); }
        public void Write(string file, string text)
        {
            var path = Path.Combine(Directory, file); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text);
        }
        public void Png(string file)
        {
            var path = Path.Combine(Directory, file); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        }
        public GuideMesh Load() => GuideMesh.Load(Path.Combine(Directory, "guide.obj"));
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
