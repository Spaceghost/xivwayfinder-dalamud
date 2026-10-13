using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using XivWayfinder.Core;
using XivVisualAssets.Animation;

namespace XivWayfinder.Plugin;

/// <summary>Imported models share the native UI boundary and scene-depth contract
/// with world guides. Model/texture loading is asynchronous; game collision and
/// scene submission remain on their respective game/UI and render threads.</summary>
internal sealed class ImportedGuide(IDalamudPluginInterface pi, IGameInteropProvider interop,
    ITextureProvider textures, IPluginLog log, GuideWorldDepth? worldDepth = null) : IDisposable
{
    private sealed record Asset(GuideMesh Mesh, IDalamudTextureWrap[] Textures, GuideJumpCapability Jump, GuideImportedGestures Gestures) : IDisposable
    { public void Dispose() { foreach (var texture in Textures) texture.Dispose(); } }
    private readonly object gate = new();
    private readonly object stageGate = new();
    private readonly GuideGpu gpu = new(worldDepth is null ? null : worldDepth.Acquire);
    private readonly GuideRenderPipeline pipeline = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private GuideUiStage? stage;
    private Asset? asset;
    private Task<Asset>? loading;
    private readonly HashSet<Task<Asset>> outstandingLoads = [];
    private CancellationTokenSource? cancellation;
    private string selected = "";
    private string[] animationNames = [];
    private bool disposed, faulted, ready, loggedSubmission;
    private Vector3[] posed = [], world = [], posedNormals = [], worldNormals = [];
    private readonly GuideSpacingRadius spacingRadius = new();
    private readonly GuideBoardingRenderCache boarding = new();
    private GuideBoardingPreparedPose? queuedBoardingPose;
    private GuideBoardingIdentity boardingIdentity;
    private readonly record struct PresentedPose(GuideMesh Mesh, VisualAnimationFrame Frame, bool Loop, bool InPlace,
        Vector3 FootPosition, Quaternion Rotation, float Scale, double At, GuideMeshPoseSnapshot? Captured=null);
    private PresentedPose? queuedOrdinaryPose, presentedOrdinaryPose;
    private GuideMeshPoseSnapshot? boardingExit;
    private double boardingExitStarted;
    private GuideVertex[] vertices = [];
    private (float Depth, int Face, bool Transparent, int Material)[] order = [];
    private readonly List<GuideBatch> batches = [];
    private GuideBatch[] submittedBatches = [];
    private Vector3? smoothedPosition;
    private Vector3 lastPosition;
    private float yaw = float.NaN;
    private float speed, bindMinimum;
    private double last;
    private VisualAnimationState? playback;
    private LocomotionClips locomotionClips;
    private bool? locomotionGrounded;
    private GuideJumpFrame? preparedJump;
    private float preparedJumpScale, preparedJumpMinimum;
    private readonly GuideGestureClock gestureClock = new();
    private readonly GuideSitController sitting = new();
    private MinionPose seatedRoot;
    private MinionPose seatedRequest;
    private readonly record struct RoutePreparation(MinionPose Pose, float Scale, Vector3 Floor, TrailSurfaceHit? Surface, double At);
    private RoutePreparation? preparedRoute;
    private Vector3? acceptedRouteFloor;
    private bool firstPlacementRay;
    private TrailSurfaceHit? placementSurface;
    private Func<Vector3, Vector3, float, TrailSurfaceHit?>? placementRaycast;
    private bool wasGesture;
    private int lastPoseClip = -1, gestureFromClip = -1;
    private float lastPoseTime, gestureFromTime, gestureExitBlend = 1;
    private string status = "Choose a local OBJ, GLB or glTF file.";
    public MinionPose? Pose { get; set; }
    public bool Grounded { get; set; }
    public bool RouteConstrained { get; set; }
    public bool PreviewDirectPlacement { get; set; }
    public GuideJumpFrame? JumpFrame { get; set; }
    public GuideGestureFrame? GestureFrame { get; set; }
    public GuideBoardingIdentity BoardingIdentity
    {
        get { lock(gate) return boardingIdentity; }
        set { lock(gate) { if(boardingIdentity!=value) {ready=false;boarding.Reset();queuedBoardingPose=null;} boardingIdentity=value; } }
    }
    public bool GestureCanMove { get { lock (gate) return sitting.CanMove; } }
    public bool Available => asset is not null && loggedSubmission && !faulted;
    public string Status => status;
    public string[] AnimationNames => animationNames;
    public string LoadedPath { get { lock (gate) return asset is null ? "" : selected; } }
    public GuideGestureCapabilities GetGestureCapabilities()
    { lock (gate) return disposed || faulted || asset is null ? default : asset.Gestures.Capabilities; }
    public GuideBoardingCapability GetBoardingCapability(float scale)
    { lock(gate) return disposed||faulted||asset is null?default:GuideFoxBoardingProfile.Capability(asset.Mesh,scale); }
    public int[] BoardingIndices()
    { lock(gate) return asset is null?[]:(int[])asset.Mesh.Indices.Clone(); }
    public GuideBoardingPreparedPose? AcceptedBoarding { get { lock(gate) return boarding.Accepted; } }
    public GuideBoardingPreparedPose? SubmittedBoarding { get { lock(gate) return boarding.Submitted; } }
    public GuideMeshPoseSnapshot? BlendBoardingPose(GuideMeshPoseSnapshot source,int clip,float time,float blend)
    { lock(gate) { try { return asset?.Mesh.CaptureFromSnapshot(source,clip,time,blend,loop:false); } catch { return null; } } }
    // Geometry-only planning output; NEVER a render/placement receipt.
    public Vector3[]? EvaluateBoardingPose(GuideMeshPoseSnapshot pose,GuideBoardingFrame frame,float scale)
    {
        lock(gate)
        {
            if(asset is null)return null;
            try
            {
                var result=new Vector3[asset.Mesh.Vertices.Length];asset.Mesh.SampleCapturedPose(pose,result);
                var minimum=result.Min(p=>p.Y);
                for(var i=0;i<result.Length;i++)result[i]=frame.Position+Vector3.Transform((result[i]-Vector3.UnitY*minimum)*scale,frame.Rotation);
                return result;
            }
            catch{return null;}
        }
    }

    /// <summary>Largest observed bind/current-pose footprint for this
    /// asset and scale, with stable outward padding. Future poses are unproven.</summary>
    public bool TryGetSpacingRadius(float scale, out float radius)
    {
        lock (gate)
        {
            radius = 0;
            return !disposed && !faulted && asset is { } current
                && spacingRadius.TryMeasureImported(current.Mesh.Vertices, posed, current.Mesh.Indices, scale, out radius,
                    MinionGlove.Orientation(0, Grounded ? 0 : Pose?.Pitch ?? 0));
        }
    }

    /// <summary>Observed bind/current-pose envelope for local preview placement;
    /// available before the first submission and independent of jump clips.</summary>
    public bool TryGetPreviewBounds(float scale, out float radius, out float height, out float minimumY)
    {
        lock (gate)
        {
            radius = height = minimumY = 0;
            if (disposed || faulted || asset is not { } current || !float.IsFinite(scale) || scale <= 0) return false;
            var low = float.PositiveInfinity; var high = float.NegativeInfinity; var squared = 0f;
            foreach (var index in current.Mesh.Indices)
            {
                if ((uint)index >= (uint)current.Mesh.Vertices.Length || !Include(current.Mesh.Vertices[index])) return false;
                if (posed.Length == current.Mesh.Vertices.Length && !Include(posed[index])) return false;
            }
            radius = MathF.Sqrt(squared)*scale*1.1f+.05f;
            height = (high-low)*scale+.05f;
            minimumY = Grounded ? 0 : low*scale;
            return float.IsFinite(radius) && float.IsFinite(height) && radius > 0 && height > 0;

            bool Include(Vector3 point)
            {
                if (!TrailPlacement.IsFinite(point)) return false;
                low = Math.Min(low,point.Y); high = Math.Max(high,point.Y);
                squared = Math.Max(squared,point.X*point.X+point.Z*point.Z);
                return true;
            }
        }
    }

    /// <summary>Explicit framework lifecycle boundary; a transient Pose=null may not reach a UI draw.</summary>
    public void ResetRoutePlacement()
    {
        lock (gate)
        {
            sitting.Reset(); gestureClock.Reset(); GestureFrame = null;
            preparedRoute = null; acceptedRouteFloor = null; smoothedPosition = null;
            preparedJump = null; wasGesture = false; gestureExitBlend = 1; ready = false;
            boarding.Reset(); BoardingIdentity = default;
            queuedOrdinaryPose = presentedOrdinaryPose = null; boardingExit=null;
        }
    }

    /// <summary>
    /// Framework preflight of this exact grounded route pose. At most one floor
    /// and two wall rays; not a full-body sweep. Queue consumes the same accepted
    /// floor instead of independently changing geometry after the arc commits.
    /// </summary>
    public unsafe bool CanRenderRoutePose(MinionPose candidate, float scale)
    {
        lock (gate)
        {
            try
            {
                var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
                if (disposed || faulted || asset is null || !Grounded || !RouteConstrained || JumpFrame is not null || boarding.Accepted is not null
                    || framework == null || framework->BGCollisionModule == null) return false;
                if (!sitting.CanMove && !GuideRoutePlacement.SameHeldPose(candidate, seatedRequest)) return false;
                if (!TryPlaceWithSurface(candidate, scale, acceptedRouteFloor, out var floor, out var surface)) return false;
                preparedRoute = new(candidate, scale, floor, surface, clock.Elapsed.TotalSeconds);
                acceptedRouteFloor = floor;
                return true;
            }
            catch (Exception ex)
            { status = "Route placement could not be verified: " + ex.Message; return false; }
        }
    }

    // Caller holds gate. Reuse the delegate: capturing the accepted floor's
    // triangle adds neither collision rays nor per-frame closure allocations.
    // GuideRoutePlacement always performs its center-floor ray first.
    private bool TryPlaceWithSurface(MinionPose candidate, float scale, Vector3? previousFloor,
        out Vector3 floor, out TrailSurfaceHit? surface)
    {
        firstPlacementRay = true; placementSurface = null;
        var accepted = GuideRoutePlacement.TryPlace(candidate, scale, previousFloor,
            placementRaycast ??= RaycastWithSurface, out floor);
        surface = accepted ? placementSurface : null;
        return accepted;
    }

    private TrailSurfaceHit? RaycastWithSurface(Vector3 from, Vector3 direction, float length)
    {
        var hit = TrailGround.Raycast(from, direction, length);
        if (firstPlacementRay) { firstPlacementRay = false; placementSurface = hit; }
        return hit;
    }

    private static bool TryPlacePreview(Vector3 at,out Vector3 floor,out TrailSurfaceHit? surface)
    {
        floor=default;
        surface=TrailGround.Raycast(at+Vector3.UnitY*.15f,-Vector3.UnitY,.3f);
        if (surface is not { } hit || !TrailPlacement.IsFinite(hit.Point)
            || Math.Abs(hit.Point.X-at.X)>.01f || Math.Abs(hit.Point.Z-at.Z)>.01f || Math.Abs(hit.Point.Y-at.Y)>.1f
            || TrailPlacement.SurfaceNormal(hit.Normal,hit.A,hit.B,hit.C).Y<.8f) return false;
        floor=hit.Point;return true;
    }

    public GuideJumpCapability GetJumpCapability(float scale)
    {
        lock (gate)
        {
            if (disposed || faulted || asset is null || !float.IsFinite(scale) || scale <= 0) return default;
            var measured = asset.Jump;
            var scaled = measured with { Radius = measured.Radius * scale, Height = measured.Height * scale };
            return scaled.Valid ? scaled : default;
        }
    }

    // The cast-ground adapter only authorizes an exact opt-in profile plus
    // fresh route, floor, avatar-exclusion and body-clearance evidence.
    // Ordinary Jump/Hop clips do not establish a boarding capability.
    public GuideMeshPoseSnapshot? CaptureBoardingPose(VisualAnimationFrame frame)
    {
        lock (gate)
        {
            if (disposed || faulted || asset is null) return null;
            try { return asset.Mesh.CapturePose(frame, loop: false, inPlace: true); }
            catch (Exception) { return null; }
        }
    }

    public GuideBoardingRenderedPose? CaptureLastPresentedPose()
    {
        lock(gate)
        {
            if(disposed || faulted || asset is null || !Grounded || !RouteConstrained || JumpFrame is not null
                || !sitting.CanMove || boarding.Accepted is not null || presentedOrdinaryPose is not { } p
                || !ReferenceEquals(p.Mesh,asset.Mesh) || BoardingNow<p.At || BoardingNow-p.At>.1) return null;
            try { return new(p.Captured??asset.Mesh.CapturePose(p.Frame,p.Loop,p.InPlace),p.FootPosition,p.Rotation,p.Scale,p.At); }
            catch(Exception) { return null; }
        }
    }

    public GuideMeshPoseSnapshot? CaptureBoardingRecovery(GuideBoardingPreparedPose from, int clip, float time, float blend)
    {
        lock (gate)
        {
            if (disposed || faulted || asset is null || from.Frame.Identity != BoardingIdentity) return null;
            try { return asset.Mesh.CaptureFromSnapshot(from.LocalPose, clip, time, blend, loop: false); }
            catch (Exception) { return null; }
        }
    }

    public bool CanRenderBoardingFrame(GuideBoardingFrame frame, GuideMeshPoseSnapshot localPose, float scale,
        GuideBoardingReceiverVolume? receiver, GuideBoardingMeshClearance? clearance, out GuideBoardingPreparedPose? receipt)
    {
        lock (gate)
        {
            receipt = null;
            if (disposed || faulted || asset is null || !Grounded || !RouteConstrained || !sitting.CanMove
                || JumpFrame is not null || frame.Identity != BoardingIdentity
                || frame.Capability.Kind != GuideJumpAnimationKind.ImportedClip || frame.Clip < 0 || frame.Clip >= asset.Mesh.Animations.Count)
                return false;
            if (!GuideBoardingPosePreparation.TryPrepare(asset.Mesh, localPose, frame, scale, receiver, clearance,
                    BoardingNow, out receipt)) return false;
            boarding.Propose(receipt!);
            return true;
        }
    }

    public bool ResolveBoardingPlacement(GuideBoardingPreparedPose receipt, bool accepted)
    { lock (gate) return boarding.Resolve(receipt, accepted); }

    public bool CancelUnsubmittedBoarding(GuideBoardingPreparedPose receipt)
    {
        lock(gate)
        {
            if(!boarding.CancelUnsubmitted(receipt))return false;
            ready=false; // No queued vertices from the abandoned proposal may draw.
            return true;
        }
    }

    public void ClearBoarding()
    // Only after an accepted supported landing or explicit hidden lifecycle.
    // Mid-air cancellation keeps the accepted receipt until recovery succeeds.
    { lock (gate) { ready=false; boarding.Reset(); boardingExit=null; BoardingIdentity = default; } }
    public void CompleteBoardingLanding()
    {
        lock(gate)
        {
            if(boarding.Accepted is not { Frame.Phase:GuideBoardingPhase.Landed } landed)return;
            boardingExit=landed.LocalPose;boardingExitStarted=BoardingNow;
            ready=false;boarding.Reset();BoardingIdentity=default;
        }
    }

    private static double BoardingNow => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Framework preflight; cache the exact authored pose before its autonomous arc is adopted.</summary>
    public bool CanRenderJumpFrame(GuideJumpFrame frame, float scale)
    {
        lock (gate)
        {
            try { return PrepareJumpPose(frame, scale); }
            catch (Exception ex) { preparedJump = null; status = "Jump animation could not be verified: " + ex.Message; return false; }
        }
    }

    private bool PrepareJumpPose(GuideJumpFrame frame, float scale)
    {
        if (disposed || faulted || asset is null || boarding.Accepted is not null || frame.Plan is not { Valid: true } || !float.IsFinite(frame.Progress)
            || frame.Progress is < 0 or > 1 || frame != frame.Plan.Sample(frame.Progress)
            || frame.Plan.Request.Capability.Kind != GuideJumpAnimationKind.ImportedClip
            || frame.Plan.Request.Capability != GetJumpCapability(scale)) return false;
        if (preparedJump == frame && preparedJumpScale == scale) return true;
        preparedJump = null;
        var mesh = asset.Mesh;
        mesh.SamplePose(asset.Jump.AnimationId, frame.ClipTime, posed, loop: false, inPlace: true, normals: posedNormals);
        if (!GuideJumpAnimation.PoseFits(posed, scale, frame.Plan.Request.Capability, out var minimum)) return false;
        preparedJump = frame; preparedJumpScale = scale; preparedJumpMinimum = minimum;
        // The jump traversal already owns fresh landing proof. Future walking
        // starts at that accepted landing, never traces back through the hop.
        if (frame.Complete) { acceptedRouteFloor = frame.FeetPosition; preparedRoute = null; }
        return true;
    }

    public void Update(string path)
    {
        path = path.Trim();
        lock (gate)
        {
            if (disposed) return;
            if (selected != path)
            {
                selected = path; ready = false; faulted = false; loggedSubmission = false;
                spacingRadius.Reset();
                asset?.Dispose(); asset = null; smoothedPosition = null; yaw = float.NaN; speed = 0; animationNames = [];
                playback = null; locomotionGrounded = null; preparedJump = null; JumpFrame = null;
                GestureFrame = null; gestureClock.Reset(); sitting.Reset(); wasGesture = false; gestureExitBlend = 1;
                preparedRoute = null; acceptedRouteFloor = null;
                boarding.Reset(); BoardingIdentity = default;
                queuedOrdinaryPose = presentedOrdinaryPose = null; boardingExit=null;
                lastPoseClip = gestureFromClip = -1; lastPoseTime = gestureFromTime = 0;
                cancellation?.Cancel(); cancellation?.Dispose(); cancellation = new();
                if (loading is { } superseded)
                    _=superseded.ContinueWith(t=>{if(t.IsCompletedSuccessfully)t.Result.Dispose();else _=t.Exception;});
                loading = null;
                if (path.Length == 0) status = "Choose a local OBJ, GLB or glTF file.";
                else
                {
                    status = "Loading geometry, textures and animations…";
                    var cancel = cancellation.Token;
                    loading = Load(path, cancel);
                    outstandingLoads.Add(loading);
                    _ = loading.ContinueWith(t => { lock (gate) outstandingLoads.Remove(t); }, TaskScheduler.Default);
                    _ = loading.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            if (loading is not { IsCompleted: true } complete) return;
            loading = null;
            if (!complete.IsCompletedSuccessfully)
            { status = "Could not load model: " + complete.Exception?.GetBaseException().Message; return; }
            asset = complete.Result;
            var mesh = asset.Mesh;
            animationNames = mesh.Animations.Select(c=>c.Name).ToArray();
            posed = new Vector3[mesh.Vertices.Length]; world = new Vector3[posed.Length];
            posedNormals = new Vector3[posed.Length]; worldNormals = new Vector3[posed.Length];
            vertices = new GuideVertex[mesh.Indices.Length]; order = new (float, int, bool, int)[mesh.Indices.Length / 3];
            bindMinimum = mesh.Vertices.Min(v => v.Y);
            playback = new VisualAnimationState(mesh.Animations.Select(c => c.DurationSeconds));
            locomotionGrounded = null; last = clock.Elapsed.TotalSeconds;
            status = $"{Path.GetFileName(selected)} · {order.Length:N0} triangles · {mesh.Animations.Count} animations";
        }
    }

    private async Task<Asset> Load(string path, CancellationToken cancel)
    {
        var acquired = new List<IDalamudTextureWrap>();
        try
        {
            var mesh = await Task.Run(() => GuideMesh.Load(path), cancel).ConfigureAwait(false);
            var jump = await Task.Run(() => GuideJumpAnimation.MeasureImported(mesh, cancel), cancel).ConfigureAwait(false);
            var sit = await Task.Run(() => GuideSitAnimation.MeasureImported(mesh, cancel), cancel).ConfigureAwait(false);
            foreach (var image in mesh.Images)
            {
                cancel.ThrowIfCancellationRequested();
                acquired.Add(await textures.CreateFromImageAsync(image.Data, "Wayfinder guide texture", cancel).ConfigureAwait(false));
            }
            // Cancellation after a texture finishes still releases its COM resource.
            cancel.ThrowIfCancellationRequested();
            return new Asset(mesh, acquired.ToArray(), jump, GuideGestureAnimation.Imported(mesh.Animations) with { Sitting = sit });
        }
        catch { foreach (var texture in acquired) texture.Dispose(); throw; }
    }

    public void Reload() { lock(gate) { if(selected.Length>0) selected="\0"; } }

    /// <summary>Retire preview reads before deleting an uncommitted downloaded
    /// pack. Superseded selections may still be finishing bounded file reads.</summary>
    public Task ClearAndWaitForLoadsAsync()
    {
        lock (gate)
        {
            Pose = null;
            Update("");
            return WaitForLoads(outstandingLoads.ToArray());
        }
        static async Task WaitForLoads(Task<Asset>[] pending)
        {
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { /* Each loader's normal retirement observes and disposes its result. */ }
        }
    }

    public void Queue(float scale, bool animate, string selectedAnimation, bool inPlace = true)
    {
        lock(stageGate)
        {
            if(disposed) return;
            try { QueueCore(scale,animate,selectedAnimation,inPlace); }
            catch(Exception ex)
            {
                lock(gate) { faulted=true; ready=false; status="Model preparation stopped: "+ex.Message; }
                log.Error(ex,"Wayfinder imported guide preparation failed");
            }
        }
    }

    private unsafe void QueueCore(float scale, bool animate, string selectedAnimation, bool inPlace = true)
    {
        // Hook disposal must occur outside gate: an in-flight render callback
        // may need that same gate to finish before the detour can retire.
        if (disposed) return;
        lock (gate)
        {
            ready = false;
            queuedOrdinaryPose = null;
            queuedBoardingPose = null;
            if (faulted || asset is null)
            { smoothedPosition = null; yaw = float.NaN; speed = 0; playback?.Reset(); gestureClock.Reset(); sitting.Reset(); wasGesture = false; gestureExitBlend = 1;
                preparedRoute = null; acceptedRouteFloor = null; return; }
            var mesh = asset.Mesh;
            int clip;
            string poseLabel;
            if (boarding.Accepted is { } acceptedBoarding)
            {
                if (!Grounded || !RouteConstrained || !animate || !sitting.CanMove || JumpFrame is not null
                    || !acceptedBoarding.CanRender(mesh, BoardingIdentity, scale, BoardingNow))
                { status = "Boarding is waiting for fresh exact-pose and receiver clearance."; return; }
                // No animation sampling, floor clamp, yaw smoothing or world
                // interpolation may change a pose after its preflight commits.
                acceptedBoarding.CopyWorldTo(world, worldNormals);
                queuedBoardingPose = acceptedBoarding;
                last = clock.Elapsed.TotalSeconds;
                playback?.Reset(); gestureClock.Reset();
                clip = acceptedBoarding.Frame.Clip; poseLabel = "Verified boarding: ";
            }
            else
            {
            VisualAnimationFrame exactFrame = default;
            bool exactLoop = false, exactInPlace = true;
            GuideMeshPoseSnapshot? exactCapture = null;
            if (Pose is not { } pose)
            {
                // A refused current/previous floor may temporarily hide the
                // actor. Keep its accepted wall origin and owned seated phase;
                // otherwise the next frame becomes an unchecked first spawn.
                // Real zone/model/preview lifecycles call ResetRoutePlacement.
                last = clock.Elapsed.TotalSeconds;
                sitting.Hold();
                if (GestureFrame is { Valid: true } heldGesture)
                    gestureClock.Update(heldGesture with { Paused = true }, 0);
                return;
            }
            var now = clock.Elapsed.TotalSeconds;
            var dt = (float)Math.Clamp(now - last, 0, .1); last = now;
            var requestedPose = pose;
            var heldForSit = !sitting.CanMove;
            if (heldForSit) pose = seatedRoot;
            var at = pose.Position;
            var jump = JumpFrame;
            if (jump is not null && heldForSit)
            { sitting.Hold(); status = "Waiting for the authored stand-up before jumping."; return; }
            if (jump is { } requestedJump)
            {
                if (!Grounded || !RouteConstrained || !animate || !PrepareJumpPose(requestedJump, scale))
                { status = "Waiting for verified own-route jump animation and body."; return; }
                at = requestedJump.FeetPosition;
            }
            else preparedJump = null;
            // Route-constrained poses already move by arc, including bends.
            // A second world-space lerp would draw a shortcut through corners.
            if (!RouteConstrained && !PreviewDirectPlacement && smoothedPosition is { } previous && Vector3.Distance(previous, at) < 8)
                at = Vector3.Lerp(previous, at, 1-MathF.Exp(-dt/.16f));
            float? groundY = null;
            TrailSurfaceHit? groundSurface = null;
            if (Grounded && jump is null)
            {
                Vector3 floor;
                if (RouteConstrained)
                {
                    if (preparedRoute is not { } approved || approved.Pose != requestedPose || approved.Scale != scale
                        || now < approved.At || now - approved.At > .1)
                    { sitting.Hold(); status = "Waiting for the exact accepted route-floor preflight."; return; }
                    floor = approved.Floor; groundSurface = approved.Surface;
                }
                else if (!(PreviewDirectPlacement ? TryPlacePreview(at,out floor,out groundSurface)
                    : TryPlaceWithSurface(pose with { Position = at }, scale, null, out floor, out groundSurface)))
                { sitting.Hold(); status = "Waiting for confirmed ground under the guide."; return; }
                if (heldForSit && Math.Abs(floor.Y - seatedRoot.Position.Y) > .08f)
                { sitting.Hold(); status = "Waiting for the accepted ground under the seated guide."; return; }
                groundY = floor.Y;
                at.Y = floor.Y - bindMinimum * scale + .03f;
            }
            var moved = smoothedPosition is null || dt <= 0 ? 0 : Vector2.Distance(new(at.X, at.Z), new(lastPosition.X,lastPosition.Z))/dt;
            speed += (moved-speed)*(1-MathF.Exp(-dt/.15f));
            smoothedPosition = lastPosition = at;
            yaw = RouteConstrained ? GuideChaseLayout.EaseYaw(yaw, pose.Yaw, dt)
                : MinionGlove.EaseAngle(yaw, pose.Yaw, dt, .18f);
            if (locomotionGrounded != Grounded)
            {
                locomotionClips = new(
                    ImportedAnimationSelection.Choose(animationNames, null, 0, Grounded),
                    ImportedAnimationSelection.Choose(animationNames, null, 1.5f, Grounded),
                    ImportedAnimationSelection.Choose(animationNames, null, 4.5f, Grounded));
                locomotionGrounded = Grounded;
            }
            var selectedClip = string.IsNullOrWhiteSpace(selectedAnimation) ? -1 : Array.IndexOf(animationNames, selectedAnimation);
            int? pinnedClip = selectedClip >= 0 ? selectedClip : null;
            var sitRequest = Grounded && RouteConstrained && animate && inPlace && pinnedClip is null ? GestureFrame : null;
            var sitSample = sitting.Update(asset.Gestures.Sitting, sitRequest, dt,
                jump is null && Grounded && RouteConstrained && animate && inPlace && pinnedClip is null && moved <= .05f);
            if (sitSample.Active && !heldForSit)
            {
                seatedRoot = pose with { Position = new(pose.Position.X, groundY ?? pose.Position.Y, pose.Position.Z), Yaw = yaw, Pitch = 0 };
                seatedRequest = requestedPose;
            }
            var gesture = jump is null && Grounded && RouteConstrained && animate && inPlace && pinnedClip is null && moved <= .05f
                && GestureFrame is { Valid: true, Kind: not GuideGestureKind.SitGround } requestedGesture && asset.Gestures.Capabilities.Supports(requestedGesture.Kind)
                ? (GuideGestureFrame?)requestedGesture : null;
            if (jump is not null)
            {
                gestureClock.Reset(); wasGesture = false; gestureExitBlend = 1;
                clip = asset.Jump.AnimationId;
                // The approved foot path owns world lift. Keep authored limb
                // motion without applying its root height a second time.
                at.Y -= preparedJumpMinimum * scale;
                playback!.Reset();
                lastPoseClip = clip; lastPoseTime = jump.Value.ClipTime;
                exactFrame = new(clip,lastPoseTime,clip,lastPoseTime,1);
            }
            else if (sitSample.Active)
            {
                gestureClock.Reset(); clip = sitSample.Clip;
                mesh.SamplePose(clip, sitSample.Time, posed, loop: false, inPlace: true, normals: posedNormals);
                lastPoseClip = clip; lastPoseTime = sitSample.Time; wasGesture = true; gestureExitBlend = 1;
                exactFrame = new(clip,lastPoseTime,clip,lastPoseTime,1);
                playback!.Reset();
                status = sitSample.Phase switch
                { GuideSitPhase.Enter => "Sitting down", GuideSitPhase.Seated => "Seated and waiting", _ => "Standing up before moving" };
            }
            else if (gesture is { } selectedGesture)
            {
                var sample = gestureClock.Update(selectedGesture, dt);
                clip = asset.Gestures.Clip(selectedGesture.Kind);
                if (sample.Restart) { gestureFromClip = lastPoseClip; gestureFromTime = lastPoseTime; }
                var duration = mesh.Animations[clip].DurationSeconds;
                var time = selectedGesture.Kind == GuideGestureKind.Wait ? sample.Time % duration : Math.Min(sample.Time, duration);
                exactFrame = new(gestureFromClip, gestureFromTime, clip, time, Math.Clamp(sample.Time / .18f, 0, 1));
                mesh.SampleBlendedPose(exactFrame,
                    posed, loop: false, inPlace: true, normals: posedNormals);
                lastPoseClip = clip; lastPoseTime = time; wasGesture = true; gestureExitBlend = 1;
                playback!.Reset();
            }
            else
            {
                gestureClock.Reset();
                if (wasGesture)
                { gestureFromClip = lastPoseClip; gestureFromTime = lastPoseTime; gestureExitBlend = 0; wasGesture = false; }
                var frame = playback!.Update(locomotionClips, speed, dt, animate, pinnedClip);
                clip = frame.ToClip;
                if (gestureExitBlend < 1)
                {
                    gestureExitBlend = Math.Min(1, gestureExitBlend + dt / .18f);
                    var toTime = clip >= 0 && mesh.Animations[clip].DurationSeconds > 0
                        ? frame.ToTime % mesh.Animations[clip].DurationSeconds : 0;
                    exactFrame = new(gestureFromClip, gestureFromTime, clip, toTime, gestureExitBlend);
                    exactInPlace = inPlace;
                    mesh.SampleBlendedPose(exactFrame,
                        posed, loop: false, inPlace: inPlace, normals: posedNormals);
                }
                else
                {
                    exactFrame = frame; exactLoop = true; exactInPlace = inPlace;
                    mesh.SampleBlendedPose(frame, posed, inPlace: inPlace, normals: posedNormals);
                }
                lastPoseClip = clip;
                lastPoseTime = clip >= 0 && mesh.Animations[clip].DurationSeconds > 0 ? frame.ToTime % mesh.Animations[clip].DurationSeconds : 0;
            }
            if(boardingExit is { } exitPose)
            {
                var blend=(float)Math.Clamp((BoardingNow-boardingExitStarted)/.18,0,1);
                var target=mesh.CapturePose(exactFrame,exactLoop,exactInPlace);
                exactCapture=mesh.BlendCapturedPoses(exitPose,target,blend);
                mesh.SampleCapturedPose(exactCapture,posed,posedNormals);
                if(blend>=1)boardingExit=null;
            }
            if (groundY is { } floorY) at.Y = floorY - (sitSample.Active || gesture is not null || gestureExitBlend < 1
                ? posed.Min(v => v.Y) : Math.Min(bindMinimum, posed.Min(v => v.Y))) * scale + .03f;
            var rotation = MinionGlove.Orientation(yaw, Grounded ? 0 : pose.Pitch);
            for (var i=0; i<posed.Length; i++)
            {
                world[i] = at + Vector3.Transform(posed[i]*scale, rotation);
                worldNormals[i] = scale == 0 ? Vector3.Zero : Vector3.Transform(posedNormals[i] * MathF.CopySign(1, scale), rotation);
            }
            // Keep the authored pose and existing flat-ground bounce, lifting
            // only when this exact collision triangle covers the full rendered
            // footprint. Uncovered terrain retains the center-floor policy;
            // never extrapolate a slope beyond the evidence or alter jump arcs.
            var footRoot = at + Vector3.Transform(Vector3.UnitY*(posed.Min(v=>v.Y)*scale),rotation);
            if (groundSurface is { } support && GuideModelSupport.TryGetLift(world, mesh.Indices, support, .03f, out var lift)
                && lift > 0)
            {
                for (var i = 0; i < world.Length; i++) world[i].Y += lift;
                footRoot.Y += lift;
            }
            queuedOrdinaryPose = new(mesh,exactFrame,exactLoop,exactInPlace,footRoot,rotation,scale,BoardingNow,exactCapture);
            poseLabel = gesture is not null ? "Authored gesture: " : animate && pinnedClip is not null ? "Pinned: " : "";
            }
            var cameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
            var camera = cameraManager == null ? null : cameraManager->GetActiveCamera();
            if (camera == null) return;
            Vector3 eye = camera->SceneCamera.Position;
            for (var f=0; f<order.Length; f++)
            {
                var m = f < mesh.FaceMaterials.Length ? mesh.FaceMaterials[f] : -1;
                var transparent = m >= 0 && m < mesh.Materials.Count && mesh.Materials[m].AlphaMode == "BLEND";
                var offset = f*3;
                var center = (world[mesh.Indices[offset]]+world[mesh.Indices[offset+1]]+world[mesh.Indices[offset+2]])/3;
                order[f] = (Vector3.DistanceSquared(center,eye),f,transparent,m);
            }
            Array.Sort(order, static (a,b) => a.Transparent != b.Transparent ? a.Transparent.CompareTo(b.Transparent)
                : a.Transparent ? b.Depth.CompareTo(a.Depth) : a.Material.CompareTo(b.Material));
            batches.Clear();
            for (var f=0; f<order.Length; f++)
            {
                var face=order[f].Face; var offset=face*3;
                var a=mesh.Indices[offset]; var b=mesh.Indices[offset+1]; var c=mesh.Indices[offset+2];
                var normal=Vector3.Cross(world[b]-world[a],world[c]-world[a]);
                normal=normal.LengthSquared()>1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
                var color=mesh.FaceColors[face];
                Write(f*3,a); Write(f*3+1,b); Write(f*3+2,c);
                void Write(int to,int from) => vertices[to]=new(world[from],worldNormals[from].LengthSquared()>0 ? worldNormals[from] : normal,
                    color*(from<mesh.VertexColors.Length ? mesh.VertexColors[from] : Vector4.One),
                    from<mesh.TextureCoordinates.Length ? mesh.TextureCoordinates[from] : Vector2.Zero);
                var material=order[f].Material>=0 && order[f].Material<mesh.Materials.Count ? mesh.Materials[order[f].Material] : null;
                nint texture=material is {BaseColorImage: >=0} && material.BaseColorImage<asset.Textures.Length
                    ? (nint)asset.Textures[material.BaseColorImage].Handle.Handle : 0;
                var cutoff=material?.AlphaMode switch { "BLEND" => -2f, "MASK" => material.AlphaCutoff, _ => -1f };
                var batch=new GuideBatch(f*3,3,texture,cutoff,material?.Unlit==true,order[f].Transparent,
                    material?.WrapU ?? 10497, material?.WrapV ?? 10497);
                if (batches.Count>0 && batches[^1] with {Start=batch.Start,Count=3} == batch)
                    batches[^1]=batches[^1] with {Count=batches[^1].Count+3};
                else batches.Add(batch);
            }
            submittedBatches=batches.ToArray(); ready=true;
            status=$"{Path.GetFileName(selected)} · {poseLabel}{(clip>=0 ? mesh.Animations[clip].Name : "Rest pose")} · {(loggedSubmission ? "Scene rendering active" : "Waiting for render stage")}";
        }
        try
        {
            stage ??= new GuideUiStage(interop, log, Render, () => pi.UiBuilder.DeviceHandle);
            stage.FindShaders(); stage.TryDrawUiFallback(); stage.NewFrame();
            if (stage.Faulted) { faulted=true; status=stage.Status; }
        }
        catch(Exception ex) { faulted=true; status="Model renderer unavailable: "+ex.Message; log.Error(ex,"Wayfinder imported guide stage failed"); }
    }

    private unsafe void Render(nint context)
    {
        lock(gate)
        {
            if(disposed || faulted || !ready || asset is null || Pose is null && boarding.Accepted is null) return;
            if(boarding.Accepted is { } currentBoarding
                && (!ReferenceEquals(currentBoarding,queuedBoardingPose)
                    || !currentBoarding.CanRender(asset.Mesh,BoardingIdentity,currentBoarding.Scale,BoardingNow))) return;
            if(boarding.Accepted is null && queuedBoardingPose is not null) return;
            try
            {
                var device=Device.Instance();
                if(device==null || device->SwapChain==null || device->SwapChain->BackBuffer==null) return;
                var before=gpu.Submissions;
                var sharedBefore=gpu.SharedDepthSubmissions;
                pipeline.TryDraw(context,(nint)device->SwapChain->BackBuffer->D3D11Texture2D,
                    nativeDevice=>gpu.Draw(nativeDevice,vertices,submittedBatches,1));
                if(gpu.Submissions>before && queuedBoardingPose is { } submittedBoarding)
                    boarding.MarkSubmitted(submittedBoarding);
                if(gpu.Submissions>before && queuedOrdinaryPose is { } submitted)
                    presentedOrdinaryPose=submitted with { At=BoardingNow };
                if(!loggedSubmission && gpu.Submissions>before)
                { loggedSubmission=true; log.Information("Wayfinder imported model native GPU draw: {Triangles} triangles, {Textures} textures, {Clips} clips, shared world depth={SharedDepth}",vertices.Length/3,asset.Textures.Length,asset.Mesh.Animations.Count,gpu.SharedDepthSubmissions>sharedBefore); }
            }
            catch(Exception ex) { faulted=true; status="Model renderer stopped: "+ex.Message; log.Error(ex,"Wayfinder imported model draw failed"); }
        }
    }
    public void Dispose()
    {
        lock(stageGate)
        {
        disposed=true; stage?.Dispose(); stage=null;
        lock(gate)
        {
            cancellation?.Cancel(); cancellation?.Dispose(); cancellation=null;
            if(loading is { } pending)
                _=pending.ContinueWith(t=>{if(t.IsCompletedSuccessfully)t.Result.Dispose();else _=t.Exception;});
            loading=null; boarding.Reset(); BoardingIdentity=default;
            queuedOrdinaryPose=presentedOrdinaryPose=null;
            asset?.Dispose(); asset=null; gpu.Dispose(); pipeline.Dispose();
        }
        }
    }
}
