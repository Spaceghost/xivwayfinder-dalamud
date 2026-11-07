using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuidePreviewPlacementTests
{
    private static readonly Vector3 Camera = new(0,2.4f,5);

    [Fact]
    public void RightAnchorBehindActualRoomWallUsesOpenLeftSide()
    {
        var room=new Geometry();room.Floor();room.WallX(1.8f);
        var placement=new GuidePreviewPlacement();
        Assert.Null(Update(placement,room,0));
        var pose=Update(placement,room,.016);
        Assert.NotNull(pose);Assert.True(pose.Value.Position.X < -2);
        Assert.InRange(pose.Value.Position.Y,-.00001f,.00001f);
        Assert.InRange(placement.Queries,1,GuidePreviewPlacement.MaximumQueries);
    }

    [Fact]
    public void NarrowRoomTriesCameraFrontAfterBothSidesAreObstructed()
    {
        var room=new Geometry();room.Floor();room.WallX(-1.4f);room.WallX(1.4f);
        var placement=new GuidePreviewPlacement();MinionPose? pose=null;
        for(var frame=0;frame<8&&pose is null;frame++)pose=Update(placement,room,frame*.016);
        Assert.NotNull(pose);Assert.InRange(Math.Abs(pose.Value.Position.X),0,.001f);
        Assert.InRange(pose.Value.Position.Z,1.49f,2.51f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingFloorOrASeparatingWallNeverProducesAReadyPose(bool separatingWall)
    {
        var room=new Geometry();
        if(separatingWall){room.Floor();room.WallZ(1);}
        var placement=new GuidePreviewPlacement();
        for(var frame=0;frame<120;frame++)
        {
            Assert.Null(Update(placement,room,frame/60d));
            Assert.InRange(placement.Queries,0,GuidePreviewPlacement.MaximumQueries);
        }
    }

    [Fact]
    public void MovementStartsAtAnchorThenReversesBeforeTheWallWithoutJumping()
    {
        var room=new Geometry();room.Floor();room.WallX(3.2f);
        var placement=new GuidePreviewPlacement();var previous=Update(placement,room,0,pace:1.5f)!.Value;
        Assert.InRange(previous.Position.X,2.499f,2.501f);Assert.InRange(previous.Position.Z,.699f,.701f);
        var moved=false;var turned=false;
        for(var frame=1;frame<240;frame++)
        {
            var pose=Update(placement,room,frame/60d,pace:1.5f);
            Assert.NotNull(pose);
            Assert.True(pose.Value.Position.X+.45f < 3.2f);
            Assert.InRange(Vector3.Distance(previous.Position,pose.Value.Position),0,.027f);
            moved|=Vector3.Distance(pose.Value.Position,new(2.5f,0,.7f))>.1f;
            turned|=pose.Value.Position.X<previous.Position.X-.001f;
            Assert.InRange(placement.Queries,0,GuidePreviewPlacement.MaximumQueries);
            previous=pose.Value;
        }
        Assert.True(moved);Assert.True(turned);
    }

    [Fact]
    public void NewlyOccludedRestingAnchorIsRevokedAndNewSpotGetsNewRelocation()
    {
        var room=new Geometry();room.Floor();var placement=new GuidePreviewPlacement();
        Assert.NotNull(Update(placement,room,0));var relocation=placement.Relocation;
        room.WallX(1.8f);
        Assert.Null(Update(placement,room,.26));
        Assert.Null(Update(placement,room,.28));
        var next=Update(placement,room,.30);
        Assert.NotNull(next);Assert.True(next.Value.Position.X<0);Assert.True(placement.Relocation>relocation);
    }

    [Fact]
    public void InvisibleCandidatesAndMalformedSupportFailClosed()
    {
        var room=new Geometry();room.Floor();var placement=new GuidePreviewPlacement();
        Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.45f,1.5f,0,0,0,room.Cast,_=>false));
        Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.45f,1.5f,0,0,.016,
            (origin,direction,distance)=>new(origin+direction*(distance+2),Vector3.UnitY,default,default,default),_=>true));
        Assert.Null(placement.Update(new(float.NaN),Camera,Vector3.UnitZ,true,.45f,1.5f,0,0,.032,room.Cast,_=>true));
        Assert.Equal(0,placement.Queries);
    }

    [Fact]
    public void FloatingPreviewAlsoRejectsUpperBodyWallAndCeiling()
    {
        var room=new Geometry();room.Floor();room.WallX(1.8f,1.5f,4);
        var placement=new GuidePreviewPlacement();
        Assert.Null(Update(placement,room,0,grounded:false));
        var pose=Update(placement,room,.016,grounded:false);
        Assert.NotNull(pose);Assert.True(pose.Value.Position.X<0);Assert.InRange(pose.Value.Position.Y,1.699f,1.701f);
        room.Horizontal(2.3f);
        Assert.Null(Update(placement,room,.3,grounded:false));
    }

    [Fact]
    public void CenteredFloatingModelChecksItsLowerHalfBelowTheAuthoredRoot()
    {
        var room=new Geometry();room.Floor();
        room.WallX(2.7f,.85f,1.3f);
        var placement=new GuidePreviewPlacement();
        Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,false,.45f,1.5f,0,0,0,room.Cast,_=>true,-.75f));
        var pose=placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,false,.45f,1.5f,0,0,.016,room.Cast,_=>true,-.75f);
        Assert.NotNull(pose);Assert.True(pose.Value.Position.X<0);
    }

    [Fact]
    public void CheckedMovingPosesStayOutsideCentralPostWhereLaggedOrbitWouldCutInside()
    {
        var room=new Geometry();room.Floor();var center=new Vector3(2.5f,0,1.3f);
        room.Box(center+new Vector3(-.15f,0,-.15f),center+new Vector3(.15f,.15f,.15f));
        var placement=new GuidePreviewPlacement();var lagged=new Vector3(2.5f,0,.7f);var oldSmoothingCollided=false;
        for(var frame=0;frame<240;frame++)
        {
            var pose=placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.3f,1.5f,4.5f,0,frame/60d,room.Cast,_=>true);
            Assert.NotNull(pose);var position=pose.Value.Position;
            Assert.InRange(Vector2.Distance(new(position.X,position.Z),new(center.X,center.Z)),.599f,.601f);
            Assert.True(DistanceToBox(position)>.3f);
            lagged=Vector3.Lerp(lagged,position,1-MathF.Exp(-(1f/60)/.16f));
            oldSmoothingCollided|=DistanceToBox(lagged)<.3f;
        }
        Assert.True(oldSmoothingCollided);
        float DistanceToBox(Vector3 p)=>new Vector2(Math.Max(0,Math.Abs(p.X-center.X)-.15f),Math.Max(0,Math.Abs(p.Z-center.Z)-.15f)).Length();
    }

    private static MinionPose? Update(GuidePreviewPlacement placement,Geometry room,double now,float pace=0,bool grounded=true)
        =>placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,grounded,.45f,1.5f,pace,0,now,room.Cast,_=>true);

    [Fact]
    public void WallOnRightAndSelectorOnLeftChooseVisibleCameraFrontSpot()
    {
        var room=new Geometry();room.Floor();room.WallX(1.8f);var placement=new GuidePreviewPlacement();
        var selector=new GuidePreviewScreenRect(new(220,330),new(320,510));
        bool VisibleBody(Vector3 body,float radius,float height)=>GuidePreviewVisibility.Clear(body,radius,height,selector,
            p=>new Vector2(500+p.X*100,500-p.Y*100));
        MinionPose? pose=null;
        for(var frame=0;frame<8&&pose is null;frame++)
            pose=placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.45f,1.5f,0,0,frame/60d,room.Cast,_=>true,
                visibleBody:VisibleBody,visibilityRevision:1);
        Assert.NotNull(pose);Assert.InRange(pose.Value.Position.X,.89f,.91f);Assert.InRange(pose.Value.Position.Z,1.99f,2.01f);
        // Moving the actual selector over an accepted spot revokes it on the
        // very next update, without waiting for the resting collision refresh.
        selector=new(new(540,330),new(645,510));
        Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.45f,1.5f,0,0,.14,room.Cast,_=>true,
            visibleBody:VisibleBody,visibilityRevision:2));
    }

    [Fact]
    public void CompactRoomCandidateRemainsVisibleWhenOldSidesAreBlockedAndFarFrontIsOffscreen()
    {
        var room=new Geometry();room.Floor();room.WallX(1.2f);var placement=new GuidePreviewPlacement();
        var selector=new GuidePreviewScreenRect(new(180,300),new(440,530));
        bool Visible(Vector3 p)=>p.Z<=1.5f;
        bool Body(Vector3 bottom,float radius,float height)=>GuidePreviewVisibility.Clear(bottom,radius,height,selector,
            p=>Visible(p)?new Vector2(500+p.X*100,500-p.Y*100):null);
        for(var frame=0;frame<8;frame++)
            Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.45f,1.5f,0,0,frame/60d,room.Cast,Visible,
                visibleBody:Body));
        MinionPose? pose=null;
        for(var frame=8;frame<GuidePreviewPlacement.CandidateCount&&pose is null;frame++)
            pose=placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.45f,1.5f,0,0,frame/60d,room.Cast,Visible,
                visibleBody:Body);
        Assert.NotNull(pose);Assert.InRange(pose.Value.Position.X,-.001f,.001f);Assert.InRange(pose.Value.Position.Z,.999f,1.001f);
        Assert.True(Body(pose.Value.Position,.45f,1.5f));
    }

    [Fact]
    public void ExhaustionReportsEveryCandidateOnceBeforeItsRetryDelay()
    {
        var placement=new GuidePreviewPlacement();var attempts=new List<GuidePreviewAttempt>();
        for(var frame=0;frame<GuidePreviewPlacement.CandidateCount;frame++)
        {
            Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.45f,1.5f,0,0,frame/60d,(_,_,_)=>null,_=>true));
            Assert.NotNull(placement.LastAttempt);attempts.Add(placement.LastAttempt.Value);
        }
        Assert.Equal(1,placement.ExhaustedCycles);Assert.Equal(22,attempts.Count);
        Assert.Contains("Try a smaller preview or turn the camera",placement.Status);
        Assert.Equal(Enumerable.Range(0,22),attempts.Select(a=>a.Candidate));
        Assert.All(attempts,a=>Assert.Equal("floor-missing",a.Reason));
        Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.45f,1.5f,0,0,GuidePreviewPlacement.CandidateCount/60d+.05,(_,_,_)=>null,_=>true));
        Assert.Null(placement.LastAttempt);Assert.Equal(1,placement.ExhaustedCycles);Assert.Equal(0,placement.Queries);
    }

    [Fact]
    public void WideGuideRejectsEveryCompactSpotInsidePlayerRootClearance()
    {
        var room=new Geometry();room.Floor();var placement=new GuidePreviewPlacement();
        var rejected=new List<GuidePreviewAttempt>();
        for(var frame=0;frame<GuidePreviewPlacement.CandidateCount;frame++)
        {
            // Only the compact area is visible; distant candidates cannot mask
            // whether its real body radius would overlap the player's root.
            Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,1,1.5f,0,0,frame/60d,room.Cast,
                p=>new Vector2(p.X,p.Z).Length()<1.3f));
            if(placement.LastAttempt is { } attempt && attempt.Candidate is >=8 and <14)rejected.Add(attempt);
        }
        Assert.Equal(6,rejected.Count);Assert.All(rejected,a=>Assert.Equal("player-clearance",a.Reason));
    }

    [Fact]
    public void MovingWideGuideReversesBeforeEnteringPlayerFootprint()
    {
        var room=new Geometry();room.Floor();var placement=new GuidePreviewPlacement();
        const float radius=2.05f;var moved=false;Vector3? previous=null;var reversed=false;
        for(var frame=0;frame<240;frame++)
        {
            var pose=placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,radius,1.5f,4.5f,0,frame/60d,room.Cast,_=>true);
            Assert.NotNull(pose);
            var at=pose.Value.Position;
            Assert.True(new Vector2(at.X,at.Z).Length()>=radius+GuidePreviewPlacement.PlayerFootprint);
            moved|=Vector3.Distance(at,new(2.5f,0,.7f))>.1f;
            if(previous is { } old)reversed|=at.X<old.X-.001f;
            previous=at;
        }
        Assert.True(moved);Assert.True(reversed);
    }

    [Fact]
    public void CameraAwaySpaceCanShowFullFoxSizedBodyWhenSelectorCoversForwardHemisphere()
    {
        var room=new Geometry();room.Floor();var placement=new GuidePreviewPlacement();
        var selector=new GuidePreviewScreenRect(new(100,465),new(900,900));
        bool Body(Vector3 bottom,float radius,float height)=>GuidePreviewVisibility.Clear(bottom,radius,height,selector,
            p=>new Vector2(500+p.X*100,500-p.Y*100+p.Z*90));
        for(var frame=0;frame<14;frame++)
            Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.9f,.9f,0,0,frame/60d,room.Cast,_=>true,
                visibleBody:Body));
        var pose=placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.9f,.9f,0,0,14/60d,room.Cast,_=>true,
            visibleBody:Body);
        Assert.NotNull(pose);Assert.InRange(pose.Value.Position.X,-.001f,.001f);
        Assert.InRange(pose.Value.Position.Z,-1.501f,-1.499f);
        Assert.True(Body(pose.Value.Position,.9f,.9f));
        Assert.True(new Vector2(pose.Value.Position.X,pose.Value.Position.Z).Length()>=.9f+GuidePreviewPlacement.PlayerFootprint);
        Assert.InRange(placement.Queries,1,GuidePreviewPlacement.MaximumQueries);
    }

    [Fact]
    public void RejectedNearbyFloorReportsMeasuredHeightWithoutRelaxingTheGate()
    {
        var room=new Geometry();room.Horizontal(.352813f);var placement=new GuidePreviewPlacement();
        Assert.Null(Update(placement,room,0));
        Assert.NotNull(placement.LastAttempt);
        Assert.StartsWith("floor-invalid-or-steep",placement.LastAttempt.Value.Reason);
        Assert.Contains("deltaY=0.352813",placement.LastAttempt.Value.Reason);
        Assert.Contains("hit=",placement.LastAttempt.Value.Reason);Assert.Contains("normal=",placement.LastAttempt.Value.Reason);
        Assert.Equal(1,placement.Queries);
    }

    [Fact]
    public void MeasuredGentleRampSupportsFoxFootprintAndExistingTriangleLift()
    {
        const float slope=.1875f;var room=new Geometry();room.Ramp(slope);var placement=new GuidePreviewPlacement();
        Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.9f,.9f,0,0,0,room.Cast,_=>true));
        Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.9f,.9f,0,0,.016,room.Cast,_=>true));
        var pose=placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.9f,.9f,0,0,.032,room.Cast,_=>true);
        Assert.NotNull(pose);var at=pose.Value.Position;
        Assert.InRange(at.X,1.599f,1.601f);Assert.InRange(at.Y,.299f,.301f);
        Assert.InRange(placement.Queries,1,GuidePreviewPlacement.MaximumQueries);
        var support=room.Cast(at+Vector3.UnitY*.15f,-Vector3.UnitY,.3f)!.Value;
        Assert.InRange(TrailPlacement.SurfaceNormal(support.Normal,support.A,support.B,support.C).Y,.9828f,.9830f);
        Vector3[] body=[at+new Vector3(-.9f,.03f,0),at+new Vector3(.9f,.03f,0),at+new Vector3(0,.8f,.2f)];
        Assert.True(GuideModelSupport.TryGetLift(body,[0,1,2],support,.03f,out var lift));
        Assert.InRange(lift,.1687f,.1688f);
        Assert.All(body,p=>Assert.True(p.Y+lift>=p.X*slope+.03f-.000001f));
    }

    [Fact]
    public void LowVerticalRiserFartherUphillIsNotHiddenByTheFirstRampIntersection()
    {
        const float slope=.1875f;var room=new Geometry();room.Ramp(slope);
        room.WallX(2.3f,2.3f*slope,2.3f*slope+.15f);
        var placement=new GuidePreviewPlacement();
        for(var frame=0;frame<3;frame++)
            Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.9f,.9f,0,0,frame/60d,room.Cast,_=>true));
        Assert.Equal("body-wall",placement.LastRejection);
        Assert.InRange(placement.Queries,1,GuidePreviewPlacement.MaximumQueries);
    }

    [Fact]
    public void RaisedTreadDoesNotBecomeContinuousGroundByMatchingItsUpNormal()
    {
        var room=new Geometry();room.Floor();room.Box(new(2.2f,0,-2),new(4,.15f,2));var placement=new GuidePreviewPlacement();
        Assert.Null(placement.Update(Vector3.Zero,Camera,Vector3.UnitZ,true,.9f,.9f,0,0,0,room.Cast,_=>true));
        Assert.StartsWith("floor-footprint",placement.LastRejection);
    }

    [Fact]
    public void RampCorrectionCannotEscapeCheckedHeadClearance()
    {
        var room=new Geometry();room.Ramp(.1875f);room.Horizontal(1.35f);var placement=new GuidePreviewPlacement();
        var camera=new Vector3(0,1.3f,5);
        for(var frame=0;frame<3;frame++)
            Assert.Null(placement.Update(Vector3.Zero,camera,Vector3.UnitZ,true,.9f,.9f,0,0,frame/60d,room.Cast,_=>true));
        Assert.Equal("ceiling",placement.LastRejection);
    }

    private sealed class Geometry
    {
        private readonly List<(Vector3 A,Vector3 B,Vector3 C)> triangles=[];
        public void Floor()=>Horizontal(0);
        public void Ramp(float slope)=>Quad(new(-10,-10*slope,-10),new(10,10*slope,-10),new(-10,-10*slope,10),new(10,10*slope,10));
        public void Horizontal(float y)=>Quad(new(-10,y,-10),new(10,y,-10),new(-10,y,10),new(10,y,10));
        public void WallX(float x,float low=0,float high=4)=>Quad(new(x,low,-10),new(x,high,-10),new(x,low,10),new(x,high,10));
        public void WallZ(float z)=>Quad(new(-10,0,z),new(10,0,z),new(-10,4,z),new(10,4,z));
        public void Box(Vector3 low,Vector3 high)
        {
            foreach(var x in new[]{low.X,high.X})Quad(new(x,low.Y,low.Z),new(x,high.Y,low.Z),new(x,low.Y,high.Z),new(x,high.Y,high.Z));
            foreach(var z in new[]{low.Z,high.Z})Quad(new(low.X,low.Y,z),new(high.X,low.Y,z),new(low.X,high.Y,z),new(high.X,high.Y,z));
            Quad(new(low.X,high.Y,low.Z),new(high.X,high.Y,low.Z),new(low.X,high.Y,high.Z),new(high.X,high.Y,high.Z));
        }
        private void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d){triangles.Add((a,b,c));triangles.Add((b,d,c));}
        // Actual two-sided ray/triangle intersections, independent of the
        // placement algorithm; walls remain walls beyond their sampled point.
        public TrailSurfaceHit? Cast(Vector3 origin,Vector3 direction,float distance)
        {
            TrailSurfaceHit? nearest=null;
            foreach(var (a,b,c) in triangles)
            {
                var e1=b-a;var e2=c-a;var cross=Vector3.Cross(direction,e2);var determinant=Vector3.Dot(e1,cross);
                if(Math.Abs(determinant)<1e-8f)continue;
                var inverse=1/determinant;var s=origin-a;var u=Vector3.Dot(s,cross)*inverse;
                if(u<0||u>1)continue;
                var q=Vector3.Cross(s,e1);var v=Vector3.Dot(direction,q)*inverse;
                if(v<0||u+v>1)continue;
                var length=Vector3.Dot(e2,q)*inverse;
                if(length<0||length>distance)continue;
                distance=length;nearest=new(origin+direction*length,Vector3.Normalize(Vector3.Cross(e1,e2)),a,b,c);
            }
            return nearest;
        }
    }
}
