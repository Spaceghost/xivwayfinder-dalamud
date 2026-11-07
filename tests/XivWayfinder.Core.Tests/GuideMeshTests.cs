using System.Numerics;
using System.Text;
namespace XivWayfinder.Core.Tests;
public sealed class GuideMeshTests
{
    [Fact] public void ObjNegativeIndicesAndPolygonAreTriangulatedAndNormalized()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".obj");
        try
        {
            File.WriteAllText(path,"v 0 0 0\nv 2 0 0\nv 2 2 0\nv 0 2 0\nf -4 -3 -2 -1\n");
            var mesh = GuideMesh.Load(path);
            Assert.Equal(new[]{0,1,2,0,2,3},mesh.Indices);
            Assert.Equal(new Vector3(-.5f,-.5f,0),mesh.Vertices[0]);
        }
        finally { File.Delete(path); }
    }
    [Theory] [InlineData("f 1 2 99")] [InlineData("f 0 1 2")]
    public void InvalidIndicesAreRejected(string face)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".obj");
        try { File.WriteAllText(path,"v 0 0 0\nv 1 0 0\nv 0 1 0\n"+face); Assert.Throws<InvalidDataException>(()=>GuideMesh.Load(path)); }
        finally { File.Delete(path); }
    }
    [Fact] public void GlbUsesNodeTransformsAndRejectsTruncatedBuffers()
    {
        var json = """{"asset":{"version":"2.0"},"buffers":[{"byteLength":36}],"bufferViews":[{"buffer":0,"byteLength":36}],"accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}],"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"nodes":[{"mesh":0,"scale":[2,1,1]}],"scenes":[{"nodes":[0]}]}""";
        var bytes = Encoding.UTF8.GetBytes(json.PadRight((json.Length+3)/4*4));
        var path = Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".glb");
        try
        {
            using(var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(0x46546C67u);w.Write(2u);w.Write((uint)(12+8+bytes.Length+8+36));
                w.Write(bytes.Length);w.Write(0x4E4F534Au);w.Write(bytes);
                w.Write(36);w.Write(0x004E4942u);
                foreach(var f in new[]{0f,0,0,1,0,0,0,1,0}) w.Write(f);
            }
            var mesh = GuideMesh.Load(path);
            Assert.Equal(new Vector3(.5f,-.25f,0),mesh.Vertices[1]);
            using(var f = File.OpenWrite(path)) f.SetLength(f.Length-4);
            Assert.Throws<InvalidDataException>(()=>GuideMesh.Load(path));
        }
        finally {File.Delete(path);}
    }
    [Fact] public void MissingCollisionNormalUsesTriangleAndRejectsWalls()
    {
        Assert.Equal(Vector3.UnitY,TrailPlacement.SurfaceNormal(Vector3.Zero,Vector3.Zero,Vector3.UnitX,Vector3.UnitZ));
        Assert.Equal(0,TrailPlacement.SurfaceNormal(Vector3.Zero,Vector3.Zero,Vector3.UnitX,Vector3.UnitY).Y);
        Assert.Equal(Vector3.Zero,TrailPlacement.SurfaceNormal(Vector3.Zero,Vector3.Zero,Vector3.Zero,Vector3.Zero));
    }
    [Theory] [InlineData(-5,1,3,4)] [InlineData(10,1,3,13)] [InlineData(1,1,-20,3.5f)]
    public void OverheadStaysAbovePlayerAndRoute(float floor,float player,float height,float expected)
        => Assert.Equal(expected,TrailPlacement.Overhead(new(0,floor,0),new(0,player,0),height).Y);
}
