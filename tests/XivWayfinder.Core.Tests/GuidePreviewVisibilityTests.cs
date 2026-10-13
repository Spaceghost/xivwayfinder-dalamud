using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuidePreviewVisibilityTests
{
    private static Vector2? Project(Vector3 point)=>new Vector2(500+point.X*100,500-point.Y*100);

    [Fact]
    public void SelectorAcrossTorsoRejectsEvenWhenHeadFeetAndAllCornersAreOutsideIt()
    {
        var selector=new GuidePreviewScreenRect(new(490,400),new(510,450));
        Assert.False(Inside(Project(Vector3.Zero)!.Value,selector));
        Assert.False(Inside(Project(Vector3.UnitY*1.5f)!.Value,selector));
        for(var corner=0;corner<8;corner++)
            Assert.False(Inside(Project(new((corner&1)==0?-.5f:.5f,(corner&2)==0?0:1.5f,(corner&4)==0?-.5f:.5f))!.Value,selector));
        Assert.False(GuidePreviewVisibility.Clear(Vector3.Zero,.5f,1.5f,selector,Project));
        Assert.Equal(GuidePreviewVisibilityResult.Selector,GuidePreviewVisibility.Evaluate(Vector3.Zero,.5f,1.5f,selector,Project));
    }

    [Fact]
    public void ShoulderOverlapIsRejectedAndEightPixelClearanceIsRequired()
    {
        Assert.False(GuidePreviewVisibility.Clear(Vector3.Zero,.5f,1.5f,new(new(530,330),new(600,520)),Project));
        Assert.False(GuidePreviewVisibility.Clear(Vector3.Zero,.5f,1.5f,new(new(558,330),new(600,520)),Project));
        Assert.True(GuidePreviewVisibility.Clear(Vector3.Zero,.5f,1.5f,new(new(559,330),new(600,520)),Project));
    }

    [Fact]
    public void PartialOffscreenOrMalformedProjectionCannotClaimVisibleBody()
    {
        var selector=new GuidePreviewScreenRect(new(800,0),new(900,600));
        Assert.False(GuidePreviewVisibility.Clear(Vector3.Zero,.5f,1.5f,selector,p=>p.X<0?null:Project(p)));
        Assert.Equal(GuidePreviewVisibilityResult.Offscreen,GuidePreviewVisibility.Evaluate(Vector3.Zero,.5f,1.5f,selector,p=>p.X<0?null:Project(p)));
        Assert.False(GuidePreviewVisibility.Clear(Vector3.Zero,.5f,1.5f,selector,_=>new Vector2(float.NaN)));
        Assert.False(GuidePreviewVisibility.Clear(Vector3.Zero,.5f,1.5f,default,Project));
    }

    [Fact]
    public void ViewportDesktopOffsetDoesNotChangeOverlapDecision()
    {
        var selector=new GuidePreviewScreenRect(new(490,400),new(510,450));var offset=new Vector2(1920,80);
        Assert.Equal(GuidePreviewVisibility.Clear(Vector3.Zero,.5f,1.5f,selector,Project),
            GuidePreviewVisibility.Clear(Vector3.Zero,.5f,1.5f,new(selector.Minimum+offset,selector.Maximum+offset),p=>Project(p)+offset));
    }

    private static bool Inside(Vector2 point,GuidePreviewScreenRect box)=>point.X>=box.Minimum.X&&point.X<=box.Maximum.X
        &&point.Y>=box.Minimum.Y&&point.Y<=box.Maximum.Y;
}
