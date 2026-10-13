using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

/// <summary>Visual-only cast-ground boarding for an explicitly SHA-bound authored
/// imported profile. Uses the existing accepted navmesh approach, actual actor
/// hitbox and all-face scene sweeps. Never issues game actions or player input.</summary>
internal sealed class GuideTeleportBoarding
{
    private readonly IClientState client;
    private readonly IObjectTable objects;
    private readonly ICondition conditions;
    private readonly ImportedGuide renderer;
    private readonly CompanionBoardingController controller;
    private GuideBoardingIdentity identity;
    private ulong playerGeneration=1,guideGeneration=1,castSequence;
    private ulong actorId;
    private nint actorAddress;
    private uint zone;
    private bool wasCasting,casting,enabled,finishNext;
    private float castElapsed,castTotal,hitbox,scale;
    private byte castType;
    private uint castId;
    private Vector3 player;
    private Vector3? destination;
    private GuideBoardingCapability capability;
    private GuideBoardingRenderedPose? entry;
    private GuideBoardingApproach takeoff;
    private GuideBoardingReceiverVolume? receiver;
    private TrailSurfaceHit? floor;
    private int[] indices=[];
    private GuideBoardingCollision? collision;
    private GuideBoardingPreparedPose? prepared;
    private GuideBoardingPreparedPose? awaitingSubmission;
    private double submittedWaitStarted;
    private Plan? plan;
    private double now;
    private ulong refusedCast;
    public string Status { get; private set; }="No cast-ground boarding.";
    private sealed class Plan(GuideBoardingRequest request,GuideBoardingTrajectory path,GuideMeshPoseSnapshot source,double started)
    {
        public GuideBoardingRequest Request=request;
        public GuideBoardingTrajectory Path=path;
        public GuideMeshPoseSnapshot Source=source;
        public double Started=started;
        public int Segment,Cursor;
        public GuideBoardingSweepSphere[]? Cover;
        public bool Complete,Blocked;
    }
    public GuideTeleportBoarding(IClientState client,IObjectTable objects,ICondition conditions,ImportedGuide renderer)
    {
        this.client=client;this.objects=objects;this.conditions=conditions;this.renderer=renderer;
        controller=new(Prepare,Validate);
    }
    public void Reset()
    {
        controller.Reset();renderer.ClearBoarding();entry=null;plan=null;prepared=awaitingSubmission=null;floor=null;receiver=null;
        destination=null;finishNext=false;guideGeneration++;wasCasting=false;
    }
    public CompanionTeleportFrame Observe(CompanionTeleportFrame cue,CompanionRouteMotionFrame motion,
        bool eligible,float requestedScale)
    {
        now=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
        capability=renderer.GetBoardingCapability(requestedScale);scale=requestedScale;
        var actor=objects.LocalPlayer;
        enabled=eligible&&capability.Valid&&client.IsLoggedIn&&client.TerritoryType!=0&&actor is not null
            &&!conditions[ConditionFlag.BetweenAreas]&&!conditions[ConditionFlag.BetweenAreas51]&&!conditions[ConditionFlag.LoggingOut];
        if(!enabled){if(controller.Active||entry is not null)Reset();return cue;}
        if(actor!.GameObjectId!=actorId||actor.Address!=actorAddress||zone!=client.TerritoryType)
        {
            Reset();actorId=actor.GameObjectId;actorAddress=actor.Address;zone=client.TerritoryType;
            playerGeneration++;castSequence=0;refusedCast=0;
        }
        var previousElapsed=castElapsed;
        player=actor.Position;hitbox=actor.HitboxRadius;castElapsed=actor.CurrentCastTime;castTotal=actor.TotalCastTime;
        castType=actor.CastActionType;castId=actor.CastActionId;
        casting=TeleportCastObservation.TryRemaining(actor.IsCasting,castType,castId,castElapsed,castTotal,out var remaining)&&remaining>0;
        if(casting&&(!wasCasting||castElapsed+.1f<previousElapsed))
        {castSequence++;destination=null;refusedCast=0;}
        wasCasting=casting;identity=new(zone,playerGeneration,guideGeneration,castSequence);
        if(casting&&destination is null&&motion.HasPosition&&float.IsFinite(hitbox)&&hitbox>0)
        {
            var direction=motion.Position-player;direction.Y=0;
            if(direction.LengthSquared()>.0001f)
                destination=player+Vector3.Normalize(direction)*(hitbox+capability.Radius+.18f);
        }
        // Only a query destination. Existing GuideApproachRouter must return a
        // complete path before it can become a rendered ground-hop landing.
        return casting&&destination is { } target?cue with { Rendezvous=target }:cue;
    }

    /// <returns>True only while this adapter owns a held takeoff/boarding pose.
    /// Caller must skip route Update/Rebase and consume its motion clock.</returns>
    public bool Update(CompanionRouteMotion motion,CompanionJumpTraversal jump)
    {
        if(!enabled||jump.Active)return false;
        // Plugin clears route ownership at the start of every framework pass.
        // Waiting for an asynchronous submission or collision lock still owns
        // this exact pose; do not let UI Queue mistake it for a free preview.
        if(entry is not null)
        {renderer.RouteConstrained=true;renderer.JumpFrame=null;renderer.GestureFrame=null;}
        if(awaitingSubmission is { } waiting)
        {
            if(ReferenceEquals(renderer.SubmittedBoarding,waiting))
            {
                var placed=controller.ResolvePlacement(true);awaitingSubmission=null;
                if(placed.GroundReturn is not null){finishNext=true;return true;}
            }
            else if(!casting||now-submittedWaitStarted>=.09)
            {
                // Renderer lock arbitrates a simultaneous native submission.
                // Never rewind a receipt that reached the GPU between checks.
                if(renderer.CancelUnsubmittedBoarding(waiting))
                {
                    controller.ResolvePlacement(false);awaitingSubmission=null;
                    if(!controller.Active)
                    {refusedCast=castSequence;renderer.ClearBoarding();entry=null;plan=null;return false;}
                }
                else {controller.HoldClock(now);return true;}
            }
            else {controller.HoldClock(now);Status="Waiting for the exact boarding pose to reach the GPU.";return true;}
        }
        if(finishNext)
        {finishNext=false;renderer.CompleteBoardingLanding();entry=null;plan=null;return false;}
        if(entry is null&&(!casting||refusedCast==castSequence||!motion.Current.HasPosition||destination is null))return false;
        using var queries=new GuideBoardingCollision();collision=queries;
        try
        {
            if(!queries.Available)return controller.Active||entry is not null;
            var route=entry is null?motion.Current.Route:takeoff.Route;
            if(route.Length<2)return false;
            var end=route[^1];
            if(entry is null&&(Heading.FlatDistance(end,destination!.Value)>.5f
                ||PathTrail.Length(route)-motion.Current.Arc>1.4f||!renderer.GestureCanMove))return false;
            floor=queries.Floor(end);
            receiver=null;
            if(floor is { } support&&GuideTeleportGroundReceiver.TryCreate(identity,capability,casting,castType,castId,
                castElapsed,castTotal,player,hitbox,route,support,entry?.Rotation??Quaternion.Identity,
                (ulong)Math.Max(1,now*1000),now,now,out var observed))receiver=observed;
            if(entry is null)
            {
                if(receiver is null||renderer.JumpFrame is not null)return false;
                renderer.RouteConstrained=true;
                var capture=renderer.CaptureLastPresentedPose();
                if(capture is null)return false;
                var approach=new GuideBoardingApproach((Vector3[])route.Clone(),motion.Current.Arc,
                    capture.Value.FootPosition,capture.Value.Rotation,true,true,now);
                if(!approach.Valid(now))return false;
                entry=capture;takeoff=approach;indices=renderer.BoardingIndices();
                renderer.BoardingIdentity=identity;
                // Recreate with the actual accepted yaw, not an invented turn.
                receiver=receiver.Value with { Anchor=receiver.Value.Anchor with { Rotation=capture.Value.Rotation } };
            }
            renderer.RouteConstrained=true;renderer.JumpFrame=null;renderer.GestureFrame=null;
            if(receiver is { } current)receiver=current with { Anchor=current.Anchor with { Rotation=entry.Value.Rotation } };
            var observation=new GuideBoardingObservation(identity,true,casting,castType,castId,castElapsed,castTotal,
                false,takeoff with { SampledAt=now },receiver?.Anchor??default,capability,renderer.GestureCanMove);
            prepared=null;
            var result=controller.Update(observation,now);
            if(result.Hide){Reset();renderer.Pose=null;return true;}
            if(result.Frame is { } frame)
            {
                var accepted=prepared is { } p&&p.Frame==frame?p:PreparePose(frame);
                if(accepted is not null)
                {
                    var committed=renderer.ResolveBoardingPlacement(accepted,true);
                    if(result.PlacementPending)
                    {
                        if(committed){awaitingSubmission=accepted;submittedWaitStarted=now;}
                        else result=controller.ResolvePlacement(false);
                    }
                }
                else if(result.PlacementPending)
                {
                    controller.ResolvePlacement(false);
                    if(!controller.Active)
                    {refusedCast=castSequence;renderer.ClearBoarding();entry=null;plan=null;return false;}
                }
            }
            else if(!controller.Active&&entry is not null&&plan is { } staging)
            {
                // Freeze the exact previously presented takeoff while bounded
                // full-curve discovery runs. No unproved airborne placement.
                var first=Frame(staging,0);
                var accepted=PreparePose(first);
                if(accepted is not null)renderer.ResolveBoardingPlacement(accepted,true);
            }
            if(plan is { Complete:false,Blocked:false })AdvancePlan();
            if(!controller.Active&&plan is { Blocked:true })
            {refusedCast=castSequence;renderer.ClearBoarding();entry=null;plan=null;return false;}
            if(!controller.Active&&(!casting||castTotal-castElapsed<1.2f))
            {refusedCast=castSequence;renderer.ClearBoarding();entry=null;plan=null;return false;}
            Status=controller.Status;
            return controller.Active||entry is not null;
        }
        finally{collision=null;}
    }

    private GuideBoardingProof Prepare(GuideBoardingRequest request,double at)
    {
        if(plan is null||plan.Request.Identity!=request.Identity||plan.Request.Capability!=request.Capability
            ||plan.Request.Returning!=request.Returning||plan.Request.Start!=request.Start||plan.Request.End!=request.End
            ||plan.Blocked&&at-plan.Started>.25)
        {
            var source=request.Returning?renderer.SubmittedBoarding?.LocalPose:entry?.LocalPose;
            if(source is null)return new(GuideJumpEvidence.Unknown,null,at);
            var duration=request.Returning ? .9f : 1.1f;
            var keys=new GuideBoardingRootKey[9];
            for(var i=0;i<keys.Length;i++)
            {
                var t=i/(float)(keys.Length-1);var root=Vector3.Lerp(request.Start,request.End,t);
                root.Y+=MathF.Sin(t*MathF.PI)*(request.Returning ? .12f : .4f);
                keys[i]=new(t*duration,root,Quaternion.Slerp(request.StartRotation,request.EndRotation,t),t*duration);
            }
            plan=new(request,new(request.Returning?capability.ReturningClip:capability.BoardingClip,keys),source,at);
        }
        return plan.Complete?new(GuideJumpEvidence.Approved,plan.Path,at):new(GuideJumpEvidence.Unknown,null,at);
    }
    private GuideBoardingFrame Frame(Plan current,float t)
    {
        var sample=current.Path.Sample(t);
        return new(current.Request.Identity,current.Request.Capability,
            current.Request.Returning?GuideBoardingPhase.Returning:GuideBoardingPhase.Boarding,
            sample.Position,sample.Rotation,current.Path.Clip,sample.ClipTime,t,current.Path);
    }
    private GuideMeshPoseSnapshot? Pose(GuideBoardingFrame frame)
    {
        if(renderer.AcceptedBoarding is { } accepted&&accepted.Frame==frame)return accepted.LocalPose;
        if(frame.Phase==GuideBoardingPhase.Holding)return renderer.CaptureBoardingPose(new(frame.Clip,frame.ClipTime,frame.Clip,frame.ClipTime,1));
        var source=plan?.Source??entry?.LocalPose;if(source is null)return null;
        var blend=Math.Clamp(frame.TrajectoryTime/.18f,0,1);blend=blend*blend*(3-2*blend);
        return renderer.BlendBoardingPose(source,frame.Clip,frame.ClipTime,blend);
    }
    private GuideBoardingPreparedPose? PreparePose(GuideBoardingFrame frame)
    {
        var pose=Pose(frame);if(pose is null)return null;
        return renderer.CanRenderBoardingFrame(frame,pose,scale,receiver,CheckMesh,out var result)?result:null;
    }
    private GuideJumpEvidence Validate(GuideBoardingFrame before,GuideBoardingFrame next)
    {prepared=PreparePose(next);return prepared is null?GuideJumpEvidence.Unknown:GuideJumpEvidence.Clear;}
    private GuideBoardingPoseProof CheckMesh(GuideBoardingFrame frame,ReadOnlySpan<Vector3> vertices,ReadOnlySpan<int> topology,
        GuideBoardingReceiverVolume? observed,double at)
    {
        if(floor is not { } support||collision is null||!AvatarClear(frame.Position)
            ||!GuideModelSupport.TryGetLift(vertices,topology,support,.03f,out var lift)||lift>.0001f)
            return new(GuideJumpEvidence.Unknown,at,0);
        var before=renderer.AcceptedBoarding;
        var old=before is null?vertices:before.WorldVertices;
        if(!GuideBoardingMeshCover.TryCreate(old,vertices,topology,support.Point.Y,out var cover))return new(GuideJumpEvidence.Unknown,at,0);
        foreach(var sphere in cover)
            if(collision.Trace(sphere.From,sphere.To,sphere.Radius,out _)!=GuideJumpEvidence.Clear)return new(GuideJumpEvidence.Unknown,at,0);
        return new(GuideJumpEvidence.Clear,at,observed?.PoseSequence??0);
    }
    private bool AvatarClear(Vector3 at)=>float.IsFinite(hitbox)&&hitbox>0
        &&Heading.FlatDistance(at,player)>=hitbox+capability.Radius+GuideBoardingAvatarExclusion.Clearance;
    private void AdvancePlan()
    {
        if(plan is not { } work||collision is null||floor is not { } support)return;
        if(now-work.Started>2){work.Blocked=true;return;}
        if(work.Cover is null)
        {
            var a=Frame(work,work.Path.Keys[work.Segment].Time);var b=Frame(work,work.Path.Keys[work.Segment+1].Time);
            if(!AvatarClear(a.Position)||!AvatarClear(b.Position)){work.Blocked=true;return;}
            var ap=Pose(a);var bp=Pose(b);
            var av=ap is null?null:renderer.EvaluateBoardingPose(ap,a,scale);
            var bv=bp is null?null:renderer.EvaluateBoardingPose(bp,b,scale);
            if(av is null||bv is null||!GuideBoardingMeshCover.TryCreate(av,bv,indices,support.Point.Y,out var cover))
            {work.Blocked=true;return;}
            work.Cover=cover;work.Cursor=0;
        }
        while(work.Cursor<work.Cover.Length)
        {
            var sphere=work.Cover[work.Cursor];var evidence=collision.Trace(sphere.From,sphere.To,sphere.Radius,out _);
            if(evidence==GuideJumpEvidence.Unknown)return;
            if(evidence!=GuideJumpEvidence.Clear){work.Blocked=true;return;}
            work.Cursor++;
        }
        work.Cover=null;work.Segment++;
        if(work.Segment>=work.Path.Keys.Length-1)work.Complete=true;
    }
}
