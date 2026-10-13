using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

/// <summary>Framework-only preview collision adapter. No scene, guide or target
/// ownership is changed by finding a display spot.</summary>
internal sealed unsafe class PreviewPlacement
{
    private readonly GuidePreviewPlacement placement = new();
    private readonly Func<Vector3,Vector3,float,TrailSurfaceHit?> cast = TrailGround.Raycast;
    private Func<Vector3,bool>? visible;
    private Func<Vector3,System.Numerics.Vector2?>? project;
    private Func<Vector3,float,float,bool>? visibleBody;
    private Func<string>? visibilityFailure;
    private string lastBodyFailure="body-viewport-or-selector";
    private GuidePreviewScreenRect? selector;
    private long visibilityRevision;
    private string? waiting;
    private Key key;
    private bool keyed;
    private bool measured;
    private float radius = .45f, height = 1.8f;
    private float minimum;
    private readonly GuidePreviewAttempt?[] attempts=new GuidePreviewAttempt?[GuidePreviewPlacement.CandidateCount];
    private readonly record struct DiagnosticKey(Key Selection,float Radius,float Height,float Minimum,GuidePreviewScreenRect Selector);
    private DiagnosticKey? diagnosticContext,loggedContext;
    private double loggedAt=double.NegativeInfinity;
    public int Relocation => placement.Relocation;
    public string Status => waiting??placement.Status;
    internal readonly record struct Key(uint Territory,string Path,uint Model,uint Npc,uint BNpc,float Scale,bool Grounded);

    public MinionPose? Update(Key identity,Vector3 player,float rotation,float pace,float turn,double now,
        IGameGui gui,float measuredRadius = 0,float measuredHeight = 0,float measuredMinimum = 0,
        GuidePreviewScreenRect? selectorBounds=null,IPluginLog? log=null)
    {
        waiting=null;
        if(selector!=selectorBounds){selector=selectorBounds;visibilityRevision++;}
        if(selector is null)
        {placement.Reset();waiting="Waiting for the model selector's layout…";return null;}
        if (!keyed || identity != key)
        { placement.Reset(); key=identity;keyed=true;measured=false;radius=.45f;height=1.8f;minimum=0; }
        // Retain measured bounds even if a temporarily blocked native preview
        // is removed. Otherwise it would alternate between real and default size.
        if (float.IsFinite(measuredRadius) && measuredRadius>0 && float.IsFinite(measuredHeight) && measuredHeight>0
            && float.IsFinite(measuredMinimum))
        {
            var nextRadius=MathF.Ceiling(measuredRadius*20)/20;
            var nextMinimum=MathF.Floor(measuredMinimum*20)/20;
            var nextHigh=MathF.Ceiling((measuredMinimum+measuredHeight+.05f)*10)/10;
            var high=measured?Math.Max(minimum+height,nextHigh):nextHigh;
            radius=measured?Math.Max(radius,nextRadius):nextRadius;
            minimum=measured?Math.Min(minimum,nextMinimum):nextMinimum;height=high-minimum;measured=true;
        }
        var framework=Framework.Instance();
        var manager=CameraManager.Instance();var camera=manager==null?null:manager->GetActiveCamera();
        if (framework==null || framework->BGCollisionModule==null || framework->BGCollisionModule->ShuttingDown
            || framework->BGCollisionModule->LoadInProgressCounter>0 || camera==null)
        { placement.Reset();return null; }
        visible ??= point=>gui.WorldToScreen(point,out _,out var inside)&&inside;
        project ??= point=>gui.WorldToScreen(point,out var at,out var inside)&&inside?at:null;
        visibleBody ??= (bottom,bodyRadius,bodyHeight)=>
        {
            var result=selector is { } bounds?GuidePreviewVisibility.Evaluate(bottom,bodyRadius,bodyHeight,bounds,project)
                :GuidePreviewVisibilityResult.InvalidBounds;
            lastBodyFailure=result switch
            {GuidePreviewVisibilityResult.Offscreen=>"body-offscreen",GuidePreviewVisibilityResult.Selector=>"selector-overlap",_=>"body-invalid-bounds"};
            return result==GuidePreviewVisibilityResult.Clear;
        };
        visibilityFailure ??= ()=>lastBodyFailure;
        var context=new DiagnosticKey(key,radius,height,minimum,selector.Value);
        if(diagnosticContext!=context){diagnosticContext=context;Array.Clear(attempts);}
        var exhausted=placement.ExhaustedCycles;
        Vector3 eye=camera->SceneCamera.Position;
        var pose=placement.Update(player,eye,new(MathF.Sin(rotation),0,MathF.Cos(rotation)),
            identity.Grounded,radius,height,pace,turn,now,cast,visible,minimum,visibleBody,visibilityRevision,visibilityFailure);
        if(placement.LastAttempt is { } attempt)attempts[attempt.Candidate]=attempt;
        if(placement.ExhaustedCycles!=exhausted && loggedContext!=context && now-loggedAt>=5 && attempts.All(a=>a.HasValue))
        {
            loggedContext=context;loggedAt=now;
            log?.Information("Wayfinder preview placement exhausted: model={Model}, npc={Npc}, bnpc={BNpc}, imported={Imported}, scale={Scale}, grounded={Grounded}, measured={Measured}, radius={Radius}, height={Height}, minimumY={Minimum}, player={Player}, camera={Camera}, selector={Selector}; candidates={Candidates}.",
                key.Model,key.Npc,key.BNpc,key.Path.Length>0,key.Scale,key.Grounded,measured,radius,height,minimum,player,eye,selector,
                string.Join("; ",attempts.Select(a=>$"{a!.Value.Candidate}: offset={a.Value.Offset} world={a.Value.Position} reason={a.Value.Reason}")));
        }
        return pose;
    }
}
