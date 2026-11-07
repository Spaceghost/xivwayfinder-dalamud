using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed partial class GuideMeshAnimationTests
{
    // Synthetic receiver evidence tests the consumer, not an assertion that a
    // real character's hand midpoint provides this measured receiving volume.
    private static GuideBoardingFrame BoardingFrame(GuideBoardingPhase phase = GuideBoardingPhase.Holding,
        Quaternion? rotation = null)
    {
        var at = new Vector3(2,3,4); var q = rotation ?? Quaternion.Identity;
        var cap = new GuideBoardingCapability(GuideJumpAnimationKind.ImportedClip,"test-profile",0,0,0,.3f,.8f,1,GuideBoardingHoldMode.Loop);
        var route = new GuideBoardingTrajectory(0,[new(0,at,q,0),new(.5f,at,q,.5f)]);
        return new(new(1,2,3,4),cap,phase,at,q,0,.5f,.5f,route);
    }
    private static GuideBoardingReceiverVolume BoardingReceiver(GuideBoardingFrame frame, double time = 10) =>
        new(frame.Identity,new(GuideBoardingTarget.Arms,GuideBoardingAnchorSource.ObservedReceivingPose,
            frame.Identity.PlayerGeneration,1,frame.Position,frame.Rotation,.3f,.8f,time),7,
            frame.Position + new Vector3(0,.4f,0),Quaternion.Identity,new(.4f,.5f,.4f));
    private static GuideBoardingPoseProof BoardingClear(GuideBoardingFrame frame, ReadOnlySpan<Vector3> points,
        ReadOnlySpan<int> triangles, GuideBoardingReceiverVolume? receiver, double now) =>
        new(GuideJumpEvidence.Clear,now,receiver?.PoseSequence ?? 0);
    private static GuideMesh BoardingMesh(GlbFixture file)
    { file.Clip("fixture boarding",0,"translation",[0,1],[0,0,0,0,0,0]); return file.Load(); }

    [Fact]
    public void BoardingPreparationProvesAndCopiesExactlyRotatedWorldVerticesWithoutResampling()
    {
        using var file = new GlbFixture(); var mesh=BoardingMesh(file);
        var capture=mesh.CapturePose(new(0,.5f,0,.5f,1),loop:false,inPlace:true);
        var frame=BoardingFrame(rotation:Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2));
        Vector3[]? checkedPoints=null;
        GuideBoardingPoseProof Verify(GuideBoardingFrame f, ReadOnlySpan<Vector3> points, ReadOnlySpan<int> triangles,
            GuideBoardingReceiverVolume? receiver,double now)
        { checkedPoints=points.ToArray(); Assert.Equal(mesh.Indices,triangles.ToArray()); return BoardingClear(f,points,triangles,receiver,now); }
        Assert.True(GuideBoardingPosePreparation.TryPrepare(mesh,capture,frame,.4f,BoardingReceiver(frame),Verify,10,out var receipt));
        var vertices=new Vector3[3]; var normals=new Vector3[3]; receipt!.CopyWorldTo(vertices,normals);
        Assert.Equal(checkedPoints,vertices);
        Near(frame.Position+new Vector3(0,0,.2f),vertices[0]);
        Near(frame.Position+new Vector3(0,0,-.2f),vertices[1]);
        Near(frame.Position+new Vector3(0,.4f,.2f),vertices[2]);
        Assert.True(receipt.CanRender(mesh,frame.Identity,.4f,10.05));
        vertices[0]=new(999); receipt.CopyWorldTo(vertices,normals);
        Assert.Equal(checkedPoints,vertices);
        Assert.Throws<ArgumentException>(()=>receipt.CopyWorldTo(new Vector3[2],normals));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void BoardingPreparationRequiresCompleteFreshReceiverAndClearanceEvidence(int bad)
    {
        using var file=new GlbFixture(); var mesh=BoardingMesh(file); var frame=BoardingFrame();
        var pose=mesh.CapturePose(new(0,0,0,0,1));
        var validReceiver=BoardingReceiver(frame);
        GuideBoardingReceiverVolume? receiver=validReceiver;
        GuideBoardingMeshClearance? verify=BoardingClear;
        if(bad==0) receiver=null;
        if(bad==1) receiver=BoardingReceiver(frame,9.8);
        if(bad==2) receiver=validReceiver with { Identity=frame.Identity with { PlayerGeneration=55 } };
        if(bad==3) receiver=validReceiver with { PoseSequence=0 };
        if(bad==4) verify=null;
        if(bad==5) receiver=validReceiver with { Anchor=validReceiver.Anchor with { Source=GuideBoardingAnchorSource.Unknown } };
        Assert.False(GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,receiver,verify,10,out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void BoardingPreparationRejectsUnknownStaleFutureMismatchedOrThrowingSceneProof(int bad)
    {
        using var file=new GlbFixture(); var mesh=BoardingMesh(file); var frame=BoardingFrame();
        var pose=mesh.CapturePose(new(0,0,0,0,1)); var receiver=BoardingReceiver(frame);
        GuideBoardingPoseProof Verify(GuideBoardingFrame f,ReadOnlySpan<Vector3> p,ReadOnlySpan<int> i,GuideBoardingReceiverVolume? r,double now)
        {
            if(bad==4) throw new InvalidOperationException();
            return new(bad==0?GuideJumpEvidence.Unknown:GuideJumpEvidence.Clear,bad==1?9.8:bad==2?10.01:10,bad==3?8ul:7ul);
        }
        Assert.False(GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,receiver,Verify,10,out _));
    }

    [Fact]
    public void BoardingPreparationChecksWholeBodyInReceiverNotJustTheRoot()
    {
        using var file=new GlbFixture(); var mesh=BoardingMesh(file); var frame=BoardingFrame();
        var pose=mesh.CapturePose(new(0,0,0,0,1));
        var receiver=BoardingReceiver(frame) with { Center=frame.Position,HalfExtents=new(.05f) };
        Assert.True(receiver.Contains(frame.Position));
        Assert.False(GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,receiver,BoardingClear,10,out _));
    }

    [Fact]
    public void BoardingPreparationPitchCannotBorrowTheUprightBodyEnvelope()
    {
        using var file=new GlbFixture(); var mesh=BoardingMesh(file);
        var frame=BoardingFrame(rotation:Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI/2));
        var pose=mesh.CapturePose(new(0,0,0,0,1));
        Assert.False(GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,BoardingReceiver(frame),BoardingClear,10,out _));
    }

    [Fact]
    public void BoardingPreparationRecoveryCanProceedAfterReceiverDisappearsButStillRequiresSceneClearance()
    {
        using var file=new GlbFixture(); var mesh=BoardingMesh(file); var frame=BoardingFrame(GuideBoardingPhase.Returning);
        var pose=mesh.CapturePose(new(0,0,0,0,1));
        Assert.True(GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,null,BoardingClear,10,out var prepared));
        Assert.True(prepared!.CanRender(mesh,frame.Identity,.4f,10.05));
        Assert.False(GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,null,null,10,out _));
    }

    [Fact]
    public void BoardingPreparationReceiptInvalidatesOnIdentityClockScaleRigOrTopologyChange()
    {
        using var file=new GlbFixture(); var mesh=BoardingMesh(file); var frame=BoardingFrame();
        var pose=mesh.CapturePose(new(0,0,0,0,1));
        Assert.True(GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,BoardingReceiver(frame),BoardingClear,10,out var p));
        Assert.False(p!.CanRender(mesh,frame.Identity,.5f,10));
        Assert.False(p.CanRender(mesh,frame.Identity with { Zone=8 },.4f,10));
        Assert.False(p.CanRender(mesh,frame.Identity,.4f,9.99));
        Assert.False(p.CanRender(mesh,frame.Identity,.4f,10.101));
        Assert.False(p.CanRender(mesh,frame.Identity,.4f,double.NaN));
        Assert.False(p.CanRender(file.Load(),frame.Identity,.4f,10));
        mesh.Indices[0]=mesh.Indices[1];
        Assert.False(p.CanRender(mesh,frame.Identity,.4f,10));
    }

    [Fact]
    public void BoardingPreparationRejectsForeignRigAndMutableProofTopology()
    {
        using var file=new GlbFixture(); var mesh=BoardingMesh(file); var frame=BoardingFrame();
        var foreign=file.Load().CapturePose(new(0,0,0,0,1));
        Assert.False(GuideBoardingPosePreparation.TryPrepare(mesh,foreign,frame,.4f,BoardingReceiver(frame),BoardingClear,10,out _));
        var pose=mesh.CapturePose(new(0,0,0,0,1));
        GuideBoardingPoseProof Verify(GuideBoardingFrame f,ReadOnlySpan<Vector3> p,ReadOnlySpan<int> i,GuideBoardingReceiverVolume? r,double now)
        { mesh.Indices[0]=mesh.Indices[1]; return BoardingClear(f,p,i,r,now); }
        Assert.False(GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,BoardingReceiver(frame),Verify,10,out _));
    }

    [Fact]
    public void BoardingRenderCacheNeverDrawsAnUnacceptedOrAbandonedProposal()
    {
        using var file=new GlbFixture(); var mesh=BoardingMesh(file); var frame=BoardingFrame();
        var pose=mesh.CapturePose(new(0,0,0,0,1));
        GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,BoardingReceiver(frame),BoardingClear,10,out var first);
        GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,BoardingReceiver(frame),BoardingClear,10,out var second);
        var cache=new GuideBoardingRenderCache(); cache.Propose(first!); Assert.Null(cache.Accepted);
        Assert.True(cache.Resolve(first!,true)); Assert.Same(first,cache.Accepted);
        cache.Propose(second!); Assert.Same(first,cache.Accepted);
        Assert.True(cache.Resolve(second!,false)); Assert.Same(first,cache.Accepted);
        cache.Propose(first!); cache.Propose(second!);
        Assert.False(cache.Resolve(first!,true)); Assert.Same(first,cache.Accepted);
        Assert.True(cache.Resolve(second!,true)); Assert.Same(second,cache.Accepted);
        cache.Reset(); Assert.Null(cache.Accepted); Assert.False(cache.Resolve(second!,true));
    }

    [Fact]
    public void BoardingPreflightIsNotSubmissionAndCancellationRestoresOnlySubmittedGeometry()
    {
        using var file=new GlbFixture(); var mesh=BoardingMesh(file); var frame=BoardingFrame();
        var pose=mesh.CapturePose(new(0,0,0,0,1));
        GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,BoardingReceiver(frame),BoardingClear,10,out var first);
        GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,BoardingReceiver(frame),BoardingClear,10,out var second);
        var cache=new GuideBoardingRenderCache(); cache.Propose(first!); cache.Resolve(first!,true);
        Assert.Null(cache.Submitted); Assert.False(cache.MarkSubmitted(second!));
        Assert.True(cache.MarkSubmitted(first!)); Assert.Same(first,cache.Submitted);
        cache.Propose(second!); cache.Resolve(second!,true);
        Assert.Same(first,cache.Submitted); Assert.True(cache.CancelUnsubmitted(second!));
        Assert.Same(first,cache.Accepted); Assert.False(cache.MarkSubmitted(second!)); // late callback cannot revive it
        cache.Propose(second!); cache.Resolve(second!,true); Assert.True(cache.MarkSubmitted(second!));
        Assert.False(cache.CancelUnsubmitted(second!)); // submission won the race: no visual rewind
        Assert.Same(second,cache.Accepted); Assert.Same(second,cache.Submitted);
        cache.Reset(); Assert.Null(cache.Submitted); Assert.False(cache.MarkSubmitted(second!));
    }

    [Fact]
    public void BoardingNeverSubmittedTakeoffCanBeCancelledWithoutAnAirborneSource()
    {
        using var file=new GlbFixture(); var mesh=BoardingMesh(file); var frame=BoardingFrame();
        var pose=mesh.CapturePose(new(0,0,0,0,1));
        GuideBoardingPosePreparation.TryPrepare(mesh,pose,frame,.4f,BoardingReceiver(frame),BoardingClear,10,out var first);
        var cache=new GuideBoardingRenderCache();cache.Propose(first!);cache.Resolve(first!,true);
        Assert.True(cache.CancelUnsubmitted(first!));Assert.Null(cache.Accepted);Assert.Null(cache.Submitted);
        Assert.False(cache.MarkSubmitted(first!));
    }
}
