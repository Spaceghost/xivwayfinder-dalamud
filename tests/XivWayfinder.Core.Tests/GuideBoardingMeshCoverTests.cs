using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuideBoardingMeshCoverTests
{
    [Theory]
    [InlineData(0)] [InlineData(.05f)] [InlineData(1)]
    public void AllFaceSpheresCoverEveryInterpolatedTriangleWithoutTouchingFloor(float travel)
    {
        Vector3[] a=[new(-.02f,.03f,-.02f),new(.02f,.03f,-.02f),new(0,.15f,.03f),
            new(-.6f,.6f,0),new(.6f,.6f,0),new(0,.8f,.3f)];
        var b=a.Select(p=>p+new Vector3(travel,.04f,0)).ToArray();int[] indices=[0,1,2,3,4,5];
        Assert.True(GuideBoardingMeshCover.TryCreate(a,b,indices,0,out var spheres));
        Assert.InRange(spheres.Length,1,GuideBoardingMeshCover.MaximumSpheres);
        Assert.All(spheres,s=>Assert.True(Math.Min(s.From.Y,s.To.Y)-s.Radius>=.001f));
        for(var triangle=0;triangle<2;triangle++)for(var frame=0;frame<=10;frame++)
            for(var i=0;i<=10;i++)for(var j=0;j<=10-i;j++)
            {
                var t=frame/10f;var x=Vector3.Lerp(a[triangle*3],b[triangle*3],t);
                var y=Vector3.Lerp(a[triangle*3+1],b[triangle*3+1],t);
                var z=Vector3.Lerp(a[triangle*3+2],b[triangle*3+2],t);
                var point=x*(1-i/10f-j/10f)+y*(i/10f)+z*(j/10f);
                Assert.Contains(spheres,s=>Vector3.Distance(point,Vector3.Lerp(s.From,s.To,t))<=s.Radius+.000001f);
            }
    }
    [Fact]
    public void FloorIntersectionAndInvalidInputCannotYieldPartialApproval()
    {
        Vector3[] a=[new(0,.03f,0),new(.01f,.03f,0),new(0,.04f,0)];
        Assert.False(GuideBoardingMeshCover.TryCreate(a,a,[0,1,99],0,out var indices));Assert.Empty(indices);
        var invalid=(Vector3[])a.Clone();invalid[1]=new(float.NaN);
        Assert.False(GuideBoardingMeshCover.TryCreate(a,invalid,[0,1,2],0,out var bad));Assert.Empty(bad);
        var buried=a.Select(p=>p-Vector3.UnitY*.04f).ToArray();
        Assert.False(GuideBoardingMeshCover.TryCreate(a,buried,[0,1,2],0,out var floor));Assert.Empty(floor);
    }
    [Fact]
    public void WideFlatFeetMustRespectBoundRatherThanExcludeWalkableFaces()
    {
        Vector3[] huge=[new(-20,.03f,-20),new(20,.03f,-20),new(0,.03f,20)];
        Assert.False(GuideBoardingMeshCover.TryCreate(huge,huge,[0,1,2],0,out var spheres));Assert.Empty(spheres);
    }
    [Fact]
    public void ClipNamesNeverAuthorizeAnUnrecognizedAsset()
    {
        var mesh=new GuideMesh([Vector3.Zero],[0,0,0],[]) { Animations=[new("Survey",1),new("Walk",1),new("Run",1),
            new("Procedural.Board.CrouchLeapSettle",1.1f),new("Procedural.Board.Held",.8f),new("Procedural.Board.ReturnLand",.9f)] };
        Assert.False(GuideFoxBoardingProfile.Capability(mesh,1.5f).Valid);
        var recognized=mesh with { SourceSha256=GuideFoxBoardingProfile.Sha256 };
        Assert.True(GuideFoxBoardingProfile.Capability(recognized,1.5f).Valid);
        Assert.False(GuideFoxBoardingProfile.Capability(recognized,1.5001f).Valid);
        Assert.False(GuideFoxBoardingProfile.Capability(recognized,float.NaN).Valid);
    }
}
