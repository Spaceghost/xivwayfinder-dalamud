using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuideTeleportGroundReceiverTests
{
    // Measured 120-Hz skinned prototype envelope + .03 padding at actual user's
    // scale. Eligibility is not complete runtime trajectory collision proof.
    private static readonly GuideBoardingCapability Fox15 = new(GuideJumpAnimationKind.ImportedClip,
        "Fox.boarding-prototype:48625dfb",3,4,5,.53971463f*1.5f+.03f,.5347375f*1.5f+.03f,.8f,GuideBoardingHoldMode.FixedPose);
    private static readonly GuideBoardingIdentity Identity = new(1,2,3,4);
    private static readonly Vector3[] Path = [new(3,0,0),new(2,0,0)];
    private static readonly TrailSurfaceHit Floor = new(Path[^1],Vector3.UnitY,new(-10,0,-10),new(10,0,-10),new(0,0,10));
    private static bool Create(out GuideBoardingReceiverVolume receiver, TrailSurfaceHit? floor=null,
        Vector3? player=null,float hitbox=.5f,uint action=5,double sampledAt=10) =>
        GuideTeleportGroundReceiver.TryCreate(Identity,Fox15,true,1,action,1,5,player??Vector3.Zero,
            hitbox,Path,floor??Floor,Quaternion.Identity,7,sampledAt,10,out receiver);

    [Fact]
    public void FoxAtOnePointFiveIsEligibleForSupportedGroundHopButNeverForArms()
    {
        Assert.True(Create(out var ground));
        Assert.Equal(GuideBoardingTarget.TeleportGround,ground.Anchor.Target);
        Assert.Equal(GuideBoardingAnchorSource.ObservedTeleportGround,ground.Anchor.Source);
        Assert.Equal(Path[^1]+Vector3.UnitY*.03f,ground.Anchor.Position);
        Assert.True(ground.Anchor.Fits(Fox15,10));
        Assert.False((ground.Anchor with { Target=GuideBoardingTarget.Arms,Source=GuideBoardingAnchorSource.ObservedReceivingPose }).Fits(Fox15,10));
        Assert.False((ground.Anchor with { Target=GuideBoardingTarget.TeleportEffect,Source=GuideBoardingAnchorSource.ObservedCastEffect }).Fits(Fox15,10));
    }

    [Theory]
    [InlineData(0)] [InlineData(6)] [InlineData(7)] [InlineData(114)]
    public void RecallAndOtherActionsNeverProduceTeleportGroundEvidence(uint action) => Assert.False(Create(out _,action:action));

    [Theory]
    [InlineData(9.899)] [InlineData(10.001)] [InlineData(double.NaN)]
    public void StaleFutureAndInvalidObservationsReject(double at) => Assert.False(Create(out _,sampledAt:at));

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(float.NaN)] [InlineData(8)]
    public void MissingOrInvalidActualAvatarHitboxRejects(float radius) => Assert.False(Create(out _,hitbox:radius));

    [Fact]
    public void ActualAvatarCylinderExcludesTheFullGuideEnvelopeNotMerelyItsCenter()
    {
        Assert.False(Create(out _,player:new(1,0,0)));
        Assert.True(Create(out var receiver));
        var trajectory=new GuideBoardingTrajectory(3,[new(0,receiver.Anchor.Position,Quaternion.Identity,0),new(1,receiver.Anchor.Position,Quaternion.Identity,1)]);
        var frame=new GuideBoardingFrame(Identity,Fox15,GuideBoardingPhase.Holding,receiver.Anchor.Position,Quaternion.Identity,4,0,1,trajectory);
        Assert.True(receiver.Valid(frame,10));
        Assert.False(receiver.Valid(frame with { Position=new(1,.03f,0) },10));
        Assert.False((receiver with { Avatar=null }).Valid(frame,10));
        Assert.False((receiver with { Avatar=receiver.Avatar!.Value with { SampledAt=9.8 } }).Valid(frame,10));
    }

    [Fact]
    public void GroundModeRequiresTheWholeConservativeFootprintInsideRealHitTriangle()
    {
        // Center hit exists, but the larger guide hangs over this small island.
        var small=Floor with { A=new(1.9f,0,-.1f),B=new(2.1f,0,-.1f),C=new(2,0,.1f) };
        Assert.False(Create(out _,floor:small));
        Assert.False(Create(out _,floor:Floor with { Point=new(2,.1f,0) }));
        Assert.False(Create(out _,floor:Floor with { Point=new(3,0,0) }));
        Assert.False(Create(out _,floor:Floor with { A=new(float.NaN) }));
    }

    [Fact]
    public void SupportedGroundModeCanStartExistingBoardingControllerButStillNeedsAuthoredTrajectoryProof()
    {
        Assert.True(Create(out var receiver));
        var origin=Path[0]+Vector3.UnitY*.03f;
        var input=new GuideBoardingObservation(Identity,true,true,1,5,1,5,false,
            new(Path,0,origin,Quaternion.Identity,true,true,10),receiver.Anchor,Fox15,true);
        var unknown=new CompanionBoardingController((request,now)=>new(GuideJumpEvidence.Unknown,null,now),(_,_)=>GuideJumpEvidence.Unknown);
        Assert.False(unknown.Update(input,10).PlacementPending);
        var controller=new CompanionBoardingController((request,now)=>new(GuideJumpEvidence.Approved,
            new GuideBoardingTrajectory(request.Capability.BoardingClip,[new(0,request.Start,request.StartRotation,0),
                new(1.1f,request.End,request.EndRotation,1.1f)]),now),(_,_)=>GuideJumpEvidence.Clear);
        var result=controller.Update(input,10);
        Assert.True(result.PlacementPending);
        Assert.Equal(origin,result.Frame!.Value.Position);
        controller.ResolvePlacement(false);
        Assert.False(controller.Active); // No pop into the destination on refusal.
    }
}
