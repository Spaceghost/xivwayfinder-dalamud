using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using XivWayfinder.Core;
using Chara = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CsQuaternion = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using CsVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;

namespace XivWayfinder.Plugin;

/// <summary>
/// Native game models and authored NPC appearances, shown as purely client-side travelling guides.
///
/// Models are selected from <c>ModelChara</c>, with optional <c>ENpcBase</c> or <c>BNpcBase</c> appearance data. A
/// local BattleChara is made with <c>ClientObjectManager.CreateBattleCharacter</c>, the way Brio and Ktisis
/// make theirs; its <c>ModelContainer.ModelCharaId</c> is set to that row, it is made untargetable, placed with
/// <c>SetPosition</c> and <c>SetRotation</c>, and drawn with <c>EnableDraw</c> once ready. Each frame it is
/// moved and turned; when tilting is on, the draw object's rotation is also written with a pitch. It is removed
/// with <c>DeleteObjectByIndex</c>.
///
/// What it is not: a companion summon. Nothing is sent to the server; the object lives only in this client's
/// memory, so no other player sees it. It is removed when not wanted (<see cref="MinionPresence"/>), at once for
/// zone changes, logout, cutscenes, group pose and a hidden UI, and on unload. Everything here must run on the
/// framework thread. Cursor, moogle and chocobo models are checked in the live client.
/// </summary>
internal sealed unsafe class GloveMinion(IDataManager data, IObjectTable objects, IPluginLog log)
{
    private const uint NoIndex = uint.MaxValue;

    private readonly MinionPresence presence = new();
    private bool resolved;
    private uint selection;
    private uint npcSelection;
    private uint bnpcSelection;
    private bool humanAnimationResolved;
    private (string Name, uint Model)[]? choices;
    public IReadOnlyList<(string Name, uint Model)> Choices => choices ??= data.GetExcelSheet<Lumina.Excel.Sheets.Companion>(ClientLanguage.English)
        .Where(row => row.Model.RowId != 0 && row.Model.ValueNullable is not null && row.Singular.ExtractText().Length > 0)
        .Select(row => (Name: row.Singular.ExtractText(), Model: row.Model.RowId))
        .OrderBy(row => MinionGlove.IsWindUpCursor(row.Name) ? 0 : row.Name.Contains("moogle", StringComparison.OrdinalIgnoreCase) ? 1 : row.Name.Contains("chocobo", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
        .ThenBy(row => row.Name).ToArray();

    public bool IsCursor => selection == 0 || Choices.Any(c => c.Model == selection && MinionGlove.IsWindUpCursor(c.Name));

    public void Select(uint id,uint npcId=0,uint bnpcId=0)
    {
        if (selection == id && npcSelection==npcId && bnpcSelection==bnpcId) return;
        Remove();
        selection = id; npcSelection=npcId; bnpcSelection=bnpcId; resolved = false; modelChara = -1; presence.Reset();
        spacingRadius.Reset();
    }
    private int modelChara = -1;
    private uint index = NoIndex;
    private Chara* spawned;
    private int frames;
    private bool drawEnabled;
    private bool drawn;
    private ushort animation;
    private readonly Dictionary<uint,Task<GuideAnimationSet>> animationPacks = [];
    private Task<GuideAnimationSet>? animationPack;
    private GuideAnimationSet locomotion;
    private bool groundBlocked;
    private float moveSpeed;
    private Vector3 position = new(float.NaN);
    private float yaw = float.NaN;
    private float pitch;
    private Task<GuideNativeJumpSet>? jumpCatalog;
    private string jumpCatalogKey = "";
    private float jumpRadius, jumpHeight;
    private readonly GuideSpacingRadius spacingRadius = new();
    private GuideJumpPlan? lastJumpPlan;
    private GuideJumpFrame? placedJumpFrame;
    public GuideJumpFrame? JumpFrame { get; set; }
    public GuideGestureFrame? GestureFrame { get; set; }
    private readonly GuideGestureClock gestureClock = new();
    private readonly GuideSitController sitting = new();
    public bool GestureCanMove => sitting.CanMove;
    private Task<GuideNativeGestures>? gestureCatalog;
    private string gestureCatalogKey = "";

    public GuideGestureCapabilities GetGestureCapabilities()
    {
        if (!drawn || spawned == null) return default;
        var model = ModelCharaId();
        if (model < 0 || !StillOurs(model)) { Forget(); return default; }
        if (spawned->DrawObject == null) return default;
        var draw = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)spawned->DrawObject;
        var root = draw->ResolveRootPath(); var variant = draw->AnimationVariant;
        var type = root.Contains("/human/", StringComparison.Ordinal) ? 1
            : root.Contains("/demihuman/", StringComparison.Ordinal) ? 2 : root.Contains("/monster/", StringComparison.Ordinal) ? 3 : 0;
        if (type == 0) return default;
        var key = root + ":" + variant;
        if (key != gestureCatalogKey)
        {
            gestureCatalogKey = key;
            gestureCatalog = Task.Run(() =>
            {
                try { return GuideNativeGestureCatalog.Read(data, root, variant, type); }
                catch (Exception ex) { log.Debug(ex, "Wayfinder could not verify this rig's stationary gestures"); return default; }
            });
        }
        return gestureCatalog is { IsCompletedSuccessfully: true } done ? done.Result.Capabilities : new(locomotion.Idle > 0, false, false);
    }

    /// <summary>Verified per-rig phase clips with correctly scaled resource-bound
    /// padding. Resource bounds do not prove the whole authored jump envelope.</summary>
    public GuideJumpCapability GetJumpCapability(float scale)
    {
        if (!drawn || spawned == null || !float.IsFinite(scale) || scale <= 0) return default;
        // This preflight runs before Update's usual ownership check. The game
        // may have removed/reused the actor since the previous framework tick.
        var model = ModelCharaId();
        if (model < 0 || !StillOurs(model)) { Forget(); return default; }
        if (spawned->DrawObject == null) return default;
        var draw = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)spawned->DrawObject;
        var root = draw->ResolveRootPath();
        var variant = draw->AnimationVariant;
        var type = root.Contains("/human/", StringComparison.Ordinal) ? 1
            : root.Contains("/demihuman/", StringComparison.Ordinal) ? 2
            : root.Contains("/monster/", StringComparison.Ordinal) ? 3 : 0;
        if (type == 0) return default;
        var key = $"{root}:{variant}:{selection}:{npcSelection}:{bnpcSelection}:{BitConverter.SingleToInt32Bits(scale)}";
        if (key != jumpCatalogKey)
        {
            jumpCatalogKey = key; jumpRadius = jumpHeight = 0;
            jumpCatalog = Task.Run(() =>
            {
                try { return GuideNativeJumpCatalog.Read(data, root, variant, type); }
                catch (Exception ex) { log.Debug(ex, "Wayfinder could not verify this model's jump phases"); return default; }
            });
        }
        if (jumpCatalog is not { IsCompletedSuccessfully: true } done || !done.Result.Valid) return default;
        var actualScale = draw->Scale;
        if (Math.Abs(actualScale.X - scale) > .001f || Math.Abs(actualScale.Y - scale) > .001f || Math.Abs(actualScale.Z - scale) > .001f) return default;
        Vector3 boundsOrigin = draw->Position;
        if (pitch != 0 || !GuideNativeBounds.MatchesUprightRoot(boundsOrigin, position, (Quaternion)draw->Rotation)) return default;
        FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds bounds = default;
        draw->ComputeAxisAlignedBounds(&bounds);
        Vector3 low = bounds.Min; Vector3 high = bounds.Max;
        if (low.X > boundsOrigin.X || high.X < boundsOrigin.X || low.Z > boundsOrigin.Z || high.Z < boundsOrigin.Z) return default;
        var factors = (NativeCharacterScale*)draw;
        if (!GuideNativeBounds.TryMeasureRootEnvelope(low, high, boundsOrigin, actualScale,
            factors->First, factors->Second, out var envelope)
            || envelope.MinimumY + ((double)boundsOrigin.Y - position.Y) < -GuideJumpPolicy.FootClearance) return default;
        // Round upward and retain the largest resource-bound padding observed.
        // This margin is not a substitute for sampling every authored pose.
        jumpRadius = Math.Max(jumpRadius, MathF.Ceiling((envelope.Radius * 1.15f + .025f) * 20) / 20);
        jumpHeight = Math.Max(jumpHeight, MathF.Ceiling((envelope.Height * 1.15f + .025f) * 20) / 20);
        var capability = new GuideJumpCapability(GuideJumpAnimationKind.NativeTimeline, done.Result.Takeoff,
            "normal/jump phases", GuideNativeJumpSet.LowHopSeconds, jumpRadius, jumpHeight);
        return capability.Valid ? capability : default;
    }

    /// <summary>For the settings window: whether the selected guide can be placed and drawn.</summary>
    public string Status { get; private set; } = "not looked for yet";

    /// <summary>The native draw object exists and is visible.</summary>
    public bool Available => drawn;

    /// <summary>The visible local actor belongs to this exact preview selection.</summary>
    public bool IsReadyFor(uint id, uint npcId, uint bnpcId = 0) => drawn && selection == id && npcSelection == npcId && bnpcSelection == bnpcId;

    /// <summary>Scaled resource-bound radius for side spacing, retained
    /// across observed poses. This does not require a supported jump animation.</summary>
    public bool TryGetSpacingRadius(out float radius)
    {
        radius = 0;
        if (!drawn || spawned == null) return false;
        // Framework preflight precedes Update: validate the slot before reading
        // any fields through a pointer the game may have removed or reused.
        var model = ModelCharaId();
        if (model < 0 || !StillOurs(model)) { Forget(); return false; }
        if (spawned->DrawObject == null) return false;
        var draw = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)spawned->DrawObject;
        FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds bounds = default;
        draw->ComputeAxisAlignedBounds(&bounds);
        var factors = (NativeCharacterScale*)draw;
        Vector3 drawScale = draw->Scale;
        if (!GuideNativeBounds.TryMeasureRootEnvelope(bounds.Min, bounds.Max, draw->Position,
            drawScale, factors->First, factors->Second, out var envelope)) return false;
        if (!GuideSpacingRadius.TryMeasureRotatedNative(envelope, (Quaternion)draw->Rotation, out var measured)) return false;
        // Match the native skeleton's multiplication order for the cache key.
        var effectiveScale = drawScale * factors->Second * factors->First;
        return spacingRadius.TryRetain(measured, effectiveScale, out radius);
    }

    public bool TryGetDrawnSize(out Vector3 size)
    {
        size = default;
        if (!drawn || spawned == null || spawned->DrawObject == null) return false;
        var draw = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)spawned->DrawObject;
        FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds bounds = default;
        draw->ComputeAxisAlignedBounds(&bounds);
        // CharacterBase vf13 adds Position to resource bounds without scaling.
        // Read the current source factors, not the skeleton transform, which may
        // still hold the previous frame's scale on the first visible preview.
        var factors = (NativeCharacterScale*)draw;
        return GuidePreviewFit.TryMeasureNativeBounds(bounds.Min, bounds.Max, draw->Scale,
            factors->First, factors->Second, out size);
    }

    // Same fields mapped by BrioCharacterBase.ScaleFactor1/2. Verified against
    // client 2026.09.15: 0x14043C460 applies Object.Scale * [0x2A0] * [0x2A4]
    // to the rendered skeleton; 0x140431560 returns the unscaled bound extents.
    [StructLayout(LayoutKind.Explicit, Size = 0x2A8)]
    private struct NativeCharacterScale
    {
        [FieldOffset(0x2A0)] public float First;
        [FieldOffset(0x2A4)] public float Second;
    }

    /// <summary>This update placed the requested pose, rather than retaining an old floor position.</summary>
    public bool PlacementAccepted { get; private set; }

    /// <summary>
    /// One framework frame. <paramref name="pose"/> is where the glove should be, null for nowhere;
    /// <paramref name="want"/> says whether it should exist and, if not, how urgently it must go.
    /// </summary>
    public void Update(MinionWant want, MinionPose? pose, float scale, bool tilt, double now, float dt, bool grounded = false, bool animate = true, float turnDegrees = 0,
        bool routeConstrained = false, bool directPlacement = false)
    {
        PlacementAccepted = false;
        try
        {
            UpdateCore(want, pose, scale, tilt, now, dt, grounded, animate, turnDegrees, routeConstrained, directPlacement);
            if (index == NoIndex) PlacementAccepted = false;
        }
        catch (Exception ex)
        {
            PlacementAccepted = false;
            log.Error(ex, "XivWayfinder: the minion glove failed; removing it");
            presence.Failed(now);
            Remove();
        }
    }

    /// <summary>Starts over after a settings change: forgets failures, so creating is tried again.</summary>
    public void Retry() => presence.Reset();

    private void UpdateCore(MinionWant want, MinionPose? pose, float scale, bool tilt, double now, float dt, bool grounded, bool animate, float turnDegrees,
        bool routeConstrained, bool directPlacement)
    {
        var model = ModelCharaId();
        if (model < 0)
        {
            Remove();
            return;
        }

        if (pose is null && want == MinionWant.Show)
            want = MinionWant.Soft;
        if (!presence.Keep(want, now))
        {
            Remove();
            return;
        }

        if (index != NoIndex && !StillOurs(model))
        {
            // the game removed it (a zone change, logout): forget it without deleting someone else's object
            log.Debug("XivWayfinder: the minion glove's object went away; forgetting it");
            Forget();
        }

        if (pose is not { } p)
        { FreezeSit(); return; } // lingering: stay where it was

        var jump = JumpFrame;
        // The route owner should gate jumps too. Fail closed if a stale caller
        // nevertheless supplies one while the actor has not stood up.
        if (jump is not null && !sitting.CanMove)
        { FreezeSit(); Status = "Waiting for the authored stand-up before jumping."; return; }
        if (jump is { } requestedJump)
        {
            if (!grounded || !animate || !routeConstrained || index == NoIndex || requestedJump.Plan is not { Valid: true }
                || !float.IsFinite(requestedJump.Progress) || requestedJump.Progress is < 0 or > 1
                || requestedJump != requestedJump.Plan.Sample(requestedJump.Progress)
                || requestedJump.Plan.Request.Capability.Kind != GuideJumpAnimationKind.NativeTimeline
                || jumpCatalog is not { IsCompletedSuccessfully: true } catalog || !catalog.Result.Valid
                || requestedJump.Plan.Request.Capability.AnimationId != catalog.Result.Takeoff)
            { FreezeJump(); Status = "Waiting for verified own-route jump animation and body."; return; }
            p = p with { Position = requestedJump.FeetPosition };
        }

        if (animationPack?.IsCompletedSuccessfully == true) locomotion=animationPack.Result;
        groundBlocked=false;
        if(index==NoIndex && grounded)
        {
            if(!TryFloor(p.Position,out var placed,directPlacement)) {Status="Waiting for walkable ground beside you."; return;}
            p=p with {Position=placed};
        }

        if (index == NoIndex)
        {
            if (!presence.MayCreate(now) || !Create(model, p, scale, now))
                return;
        }

        var chara = spawned;
        frames++;
        var previousPosition = position;
        var heldForSit = !sitting.CanMove;
        var rejectedSitMovement = heldForSit && (Vector2.DistanceSquared(new(p.Position.X, p.Position.Z), new(position.X, position.Z)) > .000001f
            || Math.Abs(p.Position.Y - position.Y) > .25f);
        if (heldForSit) p = p with { Position = position, Yaw = yaw, Pitch = 0 };
        // The independent companion integrator already advances along route
        // arc. World-space lerp here would cut the inside of a route corner.
        var next = routeConstrained || directPlacement ? p.Position : MinionGlove.EasePosition(position, p.Position, dt, 0.16f, 8f);
        if (grounded && (jump is null || jump.Value.Complete))
        {
            // Do not slide horizontally over a missing floor while retaining an old height.
            // Stay on the last confirmed surface if a ledge or wall interrupts the route.
            if(!TryFloor(next,out next,directPlacement) || CrossesWall(previousPosition,next))
            {
                groundBlocked=true;
                if (jump is not null)
                { FreezeJump(); Status = "Waiting for the confirmed jump landing."; return; }
                if(!TryFloor(previousPosition,out next,directPlacement))
                {
                    FreezeSit();
                    chara->DisableDraw();drawEnabled=drawn=false;
                    Status="Waiting for walkable ground beside you.";
                    return;
                }
            }
            if (heldForSit && Vector3.DistanceSquared(next, previousPosition) > .0001f)
            {
                FreezeSit(); chara->DisableDraw(); drawEnabled = drawn = false;
                Status = "Waiting for the accepted ground under the seated guide."; return;
            }
        }
        position = next;
        var movement = new Vector2(next.X-previousPosition.X,next.Z-previousPosition.Z);
        var speed = dt > .001f ? movement.Length()/dt : 0;
        moveSpeed = groundBlocked ? 0 : Pulse.Approach(moveSpeed,speed,dt,.15f);
        // Route yaw already includes its bounded glance and the reachable
        // segment tangent. Frame displacement can span a corner and would
        // otherwise point through its inside edge; do not derive it again.
        var facing = routeConstrained ? p.Yaw
            : moveSpeed > .2f && movement.LengthSquared() > 1e-7f && model != MinionGlove.MeasuredModelChara
            ? MathF.Atan2(movement.X,movement.Y) + turnDegrees*MathF.PI/180f : p.Yaw;
        yaw = routeConstrained ? GuideChaseLayout.EaseYaw(yaw, facing, dt)
            : MinionGlove.EaseAngle(yaw, facing, dt, 0.12f);
        // Grounded route/jump clearance is yaw-only. Establish exact upright
        // orientation on this update, rather than asymptotically retaining an
        // old floating tilt that keeps the next preflight ineligible forever.
        pitch = grounded && routeConstrained || jump is not null ? 0f
            : tilt ? Pulse.Approach(pitch, p.Pitch, dt, 0.15f) : 0f;
        chara->SetPosition(position.X, position.Y, position.Z);
        chara->SetRotation(yaw);
        PlacementAccepted = !groundBlocked && !rejectedSitMovement;
        if (!drawEnabled && frames >= 2 && chara->IsReadyToDraw())
        {
            // one frame after creating it, as Brio and Ktisis do, so its model id and place are in before it loads
            chara->EnableDraw();
            drawEnabled = true;
            Status = $"Guide model {model}: waiting for its draw object.";
        }

        var draw = chara->DrawObject;
        drawn = draw != null && draw->IsVisible;
        var motion=locomotion.Choose(moveSpeed,animate && model!=MinionGlove.MeasuredModelChara);
        if (drawn) Status = groundBlocked ? "Guide ready · Waiting at an obstacle" : !grounded
            ? motion.Moving ? "Guide ready · Gliding" : "Guide ready · Floating"
            : !motion.Moving ? moveSpeed>.2f && animate ? "Guide ready · This model has no travelling animation" : "Guide ready · At rest"
            : moveSpeed<3 ? "Guide ready · Walking" : "Guide ready · Running";
        // Visibility is not a loading test: a fully loaded model can be culled.
        // A visible actor is also success even for a nonstandard resource layout.
        if (presence.Loading && presence.CheckLoadTimeout(drawn || DrawResourcesLoaded(draw), now))
        {
            log.Warning("Wayfinder model {Model} did not finish loading: ready={Ready}, flags={Flags}, draw={Draw}", model, chara->IsReadyToDraw(), chara->RenderFlags, (nint)draw);
            Remove();
            Status = presence.GaveUp ? "The game did not finish loading this guide; choose another model"
                : "The game did not finish loading this guide; trying again shortly";
            return;
        }
        if (frames > 2 && draw != null)
        {
            if(!humanAnimationResolved && ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)draw)->GetModelType()==FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.ModelType.Human)
            {
                humanAnimationResolved=true;
                var characterBase=(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)draw;
                var root=characterBase->ResolveRootPath();
                var variant=characterBase->AnimationVariant;
                log.Debug("Wayfinder character guide animation root: {Root}, variant {Variant}",root,variant);
                animationPack=Task.Run(()=>
                {
                    try{return GuideAnimations.ReadHuman(data,root,variant);}
                    catch(Exception ex){log.Debug(ex,"Wayfinder could not read character movement clips");return default;}
                });
            }
            // Only play a clip verified in this model's resident animation pack. A moogle's
            // movement clip is flight; a chocobo chick's is its own running gait.
            var timeline=jump is { } jumpPose ? jumpCatalog!.Result.Timeline(jumpPose.Phase) : motion.Timeline;
            GuideGestureFrame? gesture = null;
            GuideGestureSample gestureSample = default;
            var sitClips = gestureCatalog is { IsCompletedSuccessfully: true } sitCatalog ? sitCatalog.Result.Sitting : default;
            var sitRequest = grounded && animate && routeConstrained && !groundBlocked ? GestureFrame : null;
            var sitSample = sitting.Update(sitClips, sitRequest, dt,
                jump is null && grounded && animate && routeConstrained && !groundBlocked && movement.LengthSquared() <= .000001f);
            if (sitSample.Active)
            {
                gestureClock.Reset(); timeline = (ushort)sitSample.Clip;
                if (sitSample.Restart) animation = 0;
                Status = sitSample.Phase switch
                { GuideSitPhase.Enter => "Guide ready · Sitting down", GuideSitPhase.Seated => "Guide ready · Seated and waiting", _ => "Guide ready · Standing up before moving" };
            }
            else if (jump is null && grounded && animate && routeConstrained && !groundBlocked && movement.LengthSquared() <= .000001f
                && GestureFrame is { Valid: true } requestedGesture && GetGestureCapabilities().Supports(requestedGesture.Kind))
            {
                // SitGround is owned exclusively by the three-phase controller.
                // It cannot fall through into an idle pretending to be sitting.
                if (requestedGesture.Kind == GuideGestureKind.SitGround) requestedGesture = requestedGesture with { Kind = GuideGestureKind.Wait };
                gesture = requestedGesture;
                gestureSample = gestureClock.Update(requestedGesture, dt);
                timeline = requestedGesture.Kind == GuideGestureKind.Beckon ? gestureCatalog!.Result.Beckon
                    : gestureCatalog is { IsCompletedSuccessfully: true } readyGestures ? readyGestures.Result.Wait : locomotion.Idle;
                if (gestureSample.Restart) animation = 0;
                if (drawn) Status = requestedGesture.Kind == GuideGestureKind.Wait ? "Guide ready · Waiting with its authored idle"
                    : gestureSample.Time < gestureCatalog!.Result.BeckonSeconds ? "Guide ready · Beckoning" : "Guide ready · Beckon complete";
            }
            else gestureClock.Reset();
            if (jump is { } startedJump && !ReferenceEquals(lastJumpPlan, startedJump.Plan)) animation = 0;
            lastJumpPlan = jump?.Plan;
            if (timeline!=0 && animation != timeline)
            {
                chara->SetMode(FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.AnimLock,0);
                chara->Timeline.BaseOverride = timeline;
                chara->Timeline.TimelineSequencer.PlayTimeline(timeline);
                animation = timeline;
                log.Debug("Wayfinder guide model {Model}: native animation {Timeline}",model,timeline);
            }
            // Sequencer slot zero owns the base pose; ActionTimeline.Slot is
            // sheet metadata and must not select the facial animation slot.
            var jumpSpeed = jump is { } advancingJump && placedJumpFrame is { } previousJump
                && ReferenceEquals(advancingJump.Plan, previousJump.Plan) && dt > 0
                ? Math.Clamp((advancingJump.ClipTime - previousJump.ClipTime) / dt, 0, 2) : 0;
            var gestureRate = gesture is { Kind: GuideGestureKind.Beckon } && gestureSample.Time >= gestureCatalog!.Result.BeckonSeconds
                ? 0 : gestureSample.Rate;
            if(timeline!=0) chara->Timeline.TimelineSequencer.SetSlotSpeed(0,jump is not null ? jumpSpeed
                : sitSample.Active ? sitSample.Rate : gesture is not null ? gestureRate : motion.Speed);
            else if(animation!=0)
                // Some native props expose a movement clip without a resident
                // idle. Stop our last clip instead of walking forever at rest.
                chara->Timeline.TimelineSequencer.SetSlotSpeed(0,0);
            placedJumpFrame = jump;
            var s = Math.Clamp(scale, 0.01f, 4f);
            draw->Object.Scale = new CsVector3(s, s, s);
            if (tilt || grounded && routeConstrained || jump is not null)
            {
                var q = MinionGlove.Orientation(yaw, pitch);
                draw->Object.Rotation = new CsQuaternion(q.X, q.Y, q.Z, q.W);
            }
        }
    }

    private void FreezeJump()
    {
        if (spawned != null && lastJumpPlan is not null)
            spawned->Timeline.TimelineSequencer.SetSlotSpeed(0, 0);
    }

    private void FreezeSit()
    {
        if (sitting.CanMove) return;
        sitting.Hold();
        if (spawned != null) spawned->Timeline.TimelineSequencer.SetSlotSpeed(0, 0);
    }

    private static bool TryFloor(Vector3 candidate,out Vector3 placed,bool directPlacement=false)
    {
        placed=candidate;
        if(!float.IsFinite(candidate.X+candidate.Y+candidate.Z)) return false;
        if(!FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule.RaycastMaterialFilter(candidate+Vector3.UnitY*(directPlacement ? .15f : 1.5f),-Vector3.UnitY,out var floor,directPlacement ? .3f : 3)) return false;
        var normal=TrailPlacement.SurfaceNormal(floor.Normal,floor.V1,floor.V2,floor.V3);
        if(normal.Y<.55f || !float.IsFinite(floor.Point.Y) || MathF.Abs(floor.Point.Y-candidate.Y)>(directPlacement ? .1f : 1.5f)) return false;
        placed.Y=floor.Point.Y+.025f;
        return true;
    }

    private static bool CrossesWall(Vector3 from,Vector3 to)
    {
        var offset=to-from;offset.Y=0;
        var distance=offset.Length();
        if(!float.IsFinite(distance) || distance<.01f || distance>8) return false;
        var direction=offset/distance;
        for(var i=0;i<2;i++)
        {
            var height=i==0?.3f:.8f;
            if(FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule.RaycastMaterialFilter(from+Vector3.UnitY*height,direction,out var hit,distance)
                && TrailPlacement.SurfaceNormal(hit.Normal,hit.V1,hit.V2,hit.V3).Y<.55f) return true;
        }
        return false;
    }

    private bool Create(int model, MinionPose pose, float scale, double now)
    {
        var com = ClientObjectManager.Instance();
        if (com == null)
            return Fail(now, "the game's client object manager is not there");
        var id = com->CreateBattleCharacter();
        if (id == NoIndex || id > ushort.MaxValue)
            return Fail(now, "no free client-side object slot");
        var chara = com->GetObjectByIndex((ushort)id);
        if (chara == null)
        {
            com->DeleteObjectByIndex((ushort)id, 0);
            return Fail(now, "the new client-side object could not be found");
        }

        index = id;
        spawned = chara;
        frames = 0;
        position = pose.Position;
        yaw = pose.Yaw;
        pitch = 0f;
        JumpFrame = null; lastJumpPlan = null; placedJumpFrame = null;
        GestureFrame = null; gestureClock.Reset(); sitting.Reset();
        if (objects.LocalPlayer is not { } player)
        { com->DeleteObjectByIndex((ushort)id, 0); Forget(); return Fail(now, "No local character to initialize the guide"); }
        // A newly allocated BattleCharacter has no initialized appearance or animation state.
        // Use the game's setup routine before changing its model, as normal client actors do.
        chara->CharacterSetup.CopyFromCharacter((Chara*)player.Address, FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterSetupContainer.CopyFlags.None);
        chara->DisableDraw();
        if(npcSelection!=0 && data.GetExcelSheet<Lumina.Excel.Sheets.ENpcBase>().GetRowOrDefault(npcSelection) is {} npc)
            GuideNpcAppearance.Apply(chara,npc);
        else if(bnpcSelection!=0 && data.GetExcelSheet<Lumina.Excel.Sheets.BNpcBase>().GetRowOrDefault(bnpcSelection) is {} battle)
            GuideNpcAppearance.Apply(chara,battle);
        chara->ModelContainer.ModelCharaId = model;
        drawEnabled = drawn = false;
        chara->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        chara->Scale = Math.Clamp(scale, 0.01f, 4f);
        chara->SetPosition(pose.Position.X, pose.Position.Y, pose.Position.Z);
        chara->SetRotation(pose.Yaw);
        presence.BeginLoading(now);
        Status = $"the guide model (ModelChara {model}) is being placed";
        log.Debug("XivWayfinder: minion glove created as client object {Index}, ModelChara {Model}", id, model);
        return true;
    }

    private static bool DrawResourcesLoaded(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject* draw)
    {
        // DrawObject.NotifyTransformChanged updates loaded objects at state 3.
        // ResourceHandle state 7 is loaded (also documented by ComputeSphereBounds).
        // Use public matched-client fields; draw visibility/culling flags are separate.
        if (draw == null || draw->LoadState != 3) return false;
        var character = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)draw;
        if (character->Skeleton == null || character->Models == null || character->SlotCount is < 1 or > 64) return false;
        for (var slot = 0; slot < character->SlotCount; slot++)
        {
            var model = character->Models[slot];
            if (model != null && model->ModelResourceHandle != null && model->ModelResourceHandle->LoadState == 7)
                return true;
        }
        return false;
    }

    private bool Fail(double now, string why)
    {
        presence.Failed(now);
        Status = presence.GaveUp ? $"{why}: could not create the guide; choose another model" : $"{why}: trying again shortly";
        log.Warning("XivWayfinder: minion glove not created: {Why}", why);
        return false;
    }

    /// <summary>Whether the slot still holds the object this made: the same slot, the same memory, the same model.</summary>
    private bool StillOurs(int model)
    {
        var com = ClientObjectManager.Instance();
        if (com == null || index > ushort.MaxValue)
            return false;
        var chara = com->GetObjectByIndex((ushort)index);
        return chara != null && chara == spawned && chara->ModelContainer.ModelCharaId == model;
    }

    /// <summary>Deletes the object if it is still there. Safe to call at any time on the framework thread.</summary>
    public void Remove()
    {
        if (index == NoIndex)
            return;
        try
        {
            var model = ModelCharaId();
            if (model >= 0 && StillOurs(model))
            {
                ClientObjectManager.Instance()->DeleteObjectByIndex((ushort)index, 0);
                log.Debug("XivWayfinder: minion glove removed (client object {Index})", index);
            }
        }
        finally
        {
            Forget();
            if (ModelCharaId() >= 0 && !presence.GaveUp)
                Status = "Your guide appears while pointing the way.";
        }
    }

    private void Forget()
    {
        index = NoIndex;
        spawned = null;
        frames = 0;
        drawEnabled = drawn = false;
        animation = 0; moveSpeed = 0;
        groundBlocked=false;
        humanAnimationResolved=false;
        JumpFrame = null; lastJumpPlan = null; placedJumpFrame = null;
        GestureFrame = null; gestureClock.Reset(); sitting.Reset();
        // Retain only managed metadata and the largest resource-bound padding for
        // this exact rig/appearance/scale. A safety cancellation must not make
        // the same jump requalify forever from its smaller idle pose.
        position = new Vector3(float.NaN);
        yaw = float.NaN;
        pitch = 0f;
    }

    /// <summary>The Wind-up Cursor's ModelChara row, from the English Companion sheet; −1 when it is not there.</summary>
    private int ModelCharaId()
    {
        if (resolved)
            return modelChara;
        resolved = true;
        try
        {
            if(npcSelection!=0)
            {
                if(data.GetExcelSheet<Lumina.Excel.Sheets.ENpcBase>().GetRowOrDefault(npcSelection) is {} npc && npc.ModelChara.ValueNullable is {} npcModel)
                {
                    modelChara=(int)npc.ModelChara.RowId;
                    LoadAnimations(npcModel);
                    Status="Character appearance selected.";
                }
                else Status="This character appearance is unavailable in your game.";
                return modelChara;
            }
            if(bnpcSelection!=0)
            {
                if(data.GetExcelSheet<Lumina.Excel.Sheets.BNpcBase>().GetRowOrDefault(bnpcSelection) is {} battle && battle.ModelChara.ValueNullable is {} battleModel)
                {
                    modelChara=(int)battle.ModelChara.RowId;
                    LoadAnimations(battleModel);
                    Status="Battle character appearance selected.";
                }
                else Status="This battle character appearance is unavailable in your game.";
                return modelChara;
            }
            if (selection != 0)
            {
                if (selection <= int.MaxValue && data.GetExcelSheet<Lumina.Excel.Sheets.ModelChara>().GetRowOrDefault(selection) is { } selected && selected.Model != 0)
                {
                    modelChara = (int)selection;
                    LoadAnimations(selected);
                    Status = $"Guide model {selection} selected.";
                }
                else Status = $"ModelChara {selection} is not a usable game model.";
                return modelChara;
            }
            foreach (var row in data.GetExcelSheet<Lumina.Excel.Sheets.Companion>(ClientLanguage.English))
            {
                if (!MinionGlove.IsWindUpCursor(row.Singular.ExtractText()))
                    continue;
                var model = row.Model.RowId;
                if (model == 0 || model > int.MaxValue || row.Model.ValueNullable is null)
                    continue;
                modelChara = (int)model;
                LoadAnimations(row.Model.Value);
                Status = $"the Wind-up Cursor model was found (Companion {row.RowId}, ModelChara {model}); the glove appears while pointing the way";
                if (row.RowId != MinionGlove.MeasuredCompanionRow || model != MinionGlove.MeasuredModelChara)
                    log.Information("XivWayfinder: the Wind-up Cursor is Companion {Row}, ModelChara {Model} in this game version", row.RowId, model);
                return modelChara;
            }

            Status = "the Wind-up Cursor minion was not found in the game data: choose another guide model";
        }
        catch (Exception ex)
        {
            Status = "the Companion sheet could not be read: choose another guide model";
            log.Warning(ex, "XivWayfinder: looking up the Wind-up Cursor failed");
        }

        return modelChara;
    }

    private void LoadAnimations(Lumina.Excel.Sheets.ModelChara model)
    {
        locomotion=default;
        if(!animationPacks.TryGetValue(model.RowId,out animationPack))
        {
            animationPack=Task.Run(()=>
            {
                try {return GuideAnimations.Read(data,model);}
                catch(Exception ex) {log.Debug(ex,"Wayfinder could not read model {Model}'s movement clips",model.RowId);return default;}
            });
            animationPacks[model.RowId]=animationPack;
        }
    }
}
