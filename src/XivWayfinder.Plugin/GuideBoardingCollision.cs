using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

/// <summary>Same typed, nonblocking SceneWrapper sphere/ray contract as the
/// established own-route jump probe. No arbitrary offsets or collision writes.
/// All faces participate: no floor-normal suppression for wide/low models.</summary>
internal sealed unsafe class GuideBoardingCollision : IDisposable
{
    private BGCollisionModule* module;
    private readonly GuideBoardingQueryBudget budget=new(Stopwatch.Frequency);
    public bool Available=>module!=null;
    public GuideBoardingCollision()
    {
        if(!OperatingSystem.IsWindows())return;
        var framework=Framework.Instance();var candidate=framework==null?null:framework->BGCollisionModule;
        if(candidate==null||candidate->ShuttingDown||candidate->LoadInProgressCounter>0
            ||!TryAcquireSRWLockShared(&candidate->UpdateTaskLock))return;
        if(candidate->ShuttingDown||candidate->LoadInProgressCounter>0||candidate->SceneManager==null
            ||candidate->SceneManager->FirstScene==null||candidate->SceneManager->NumScenes is <1 or >4)
        {ReleaseSRWLockShared(&candidate->UpdateTaskLock);return;}
        module=candidate;
    }
    public GuideJumpEvidence Trace(Vector3 from,Vector3 to,float radius,out RaycastHit nearest)
    {
        nearest=default;
        if(module==null||!budget.Available
            ||!TrailPlacement.IsFinite(from)||!TrailPlacement.IsFinite(to)||!float.IsFinite(radius)||radius<0)
            return GuideJumpEvidence.Unknown;
        var delta=to-from;var distance=delta.Length();
        if(distance<.00001f){delta=Vector3.UnitY*.00001f;distance=.00001f;}
        var direction=delta/distance;var origin=new Vector4(from,radius);
        var filter=new RaycastMaterialFilter{Mask=0x4000,Value=0x4000};
        var parameters=new RaycastParams{Origin=&origin,Direction=&direction,MaxDistance=&distance,
            Algorithm=4|(radius>0?1:0),MaxPlaneNormalY=.55f,MaterialFilter=&filter};
        var found=false;var count=0;
        for(var scene=module->SceneManager->FirstScene;scene!=null;scene=(SceneWrapper*)scene->Next)
        {
            if(++count>4||scene->Scene==null||scene->VirtualTable==null
                ||!budget.TryBegin(Stopwatch.GetTimestamp()))return GuideJumpEvidence.Unknown;
            RaycastHit hit=default;
            bool collided=false,loading=false,withinBudget=false;
            try { loading=scene->HasLoadingColliders(); if(!loading)collided=scene->Raycast(&hit,1,&parameters); }
            finally { withinBudget=budget.Complete(Stopwatch.GetTimestamp()); }
            if(loading||!withinBudget)return GuideJumpEvidence.Unknown;
            if(!collided)continue;
            if(!float.IsFinite(hit.Distance)||!TrailPlacement.IsFinite(hit.Point))return GuideJumpEvidence.Unknown;
            if(!found||hit.Distance<nearest.Distance){nearest=hit;found=true;}
        }
        return count==module->SceneManager->NumScenes?found?GuideJumpEvidence.Blocked:GuideJumpEvidence.Clear:GuideJumpEvidence.Unknown;
    }
    public TrailSurfaceHit? Floor(Vector3 expected)
    {
        var status=Trace(expected+Vector3.UnitY*.5f,expected-Vector3.UnitY*.6f,0,out var hit);
        return status==GuideJumpEvidence.Blocked&&Math.Abs(hit.Point.Y-expected.Y)<.1f
            ?new(hit.Point,hit.Normal,hit.V1,hit.V2,hit.V3):null;
    }
    public void Dispose(){if(module!=null){ReleaseSRWLockShared(&module->UpdateTaskLock);module=null;}}
    [DllImport("kernel32.dll",ExactSpelling=true)] [return:MarshalAs(UnmanagedType.U1)]
    private static extern bool TryAcquireSRWLockShared(nint* srwLock);
    [DllImport("kernel32.dll",ExactSpelling=true)] private static extern void ReleaseSRWLockShared(nint* srwLock);
}
