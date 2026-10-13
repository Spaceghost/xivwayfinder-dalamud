using System.Numerics;
using System.Diagnostics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

internal readonly record struct FairyTrailParticle(Vector3 Position, float Radius, float Opacity,
    Vector3 GroundNormal = default, float PixelRadius = 0);
internal readonly record struct NativeTrailSegment(Vector3 A, Vector3 B, Vector3 GroundNormalA, Vector3 GroundNormalB,
    float WidthPixels, float Opacity, float Glow, float Pulse);

/// <summary>
/// Untextured radial glows and classic trail ribbons against scene depth before native UI.
/// Queue consumes its span synchronously at UiBuilder.Draw; no caller-owned
/// memory or ImGui state reaches the native callback. This pass does not share
/// the imported model's private depth buffer, so those two custom passes do not
/// mutually depth-test. Game geometry and game characters still occlude it.
/// </summary>
internal sealed class NativeFairyTrail(IDalamudPluginInterface pi, IGameInteropProvider interop,
    IPluginLog log) : IDisposable
{
    internal const int MaximumParticles = 256;
    internal const int MaximumSegments = 127;
    internal const int TrianglesPerParticle = 60;
    internal const float MaximumRadius = TrailGeometry.MaximumRadius;
    internal const float MaximumDistance = 80;
    private const int Sectors = 12;
    private const int VerticesPerParticle = TrianglesPerParticle * 3;
    private const int MaximumVertices = GuideMesh.MaxTriangles * 3;
    private readonly record struct PreparedPrimitive(int Start, int Count, float DistanceSquared);
    private readonly record struct OrderedParticle(FairyTrailParticle Particle, float DistanceSquared, int Index);
    private readonly record struct OrderedSegment(NativeTrailSegment Segment, float DistanceSquared, int Index);
    private static readonly Vector2[] Directions = MakeDirections();
    private readonly object gate = new();
    private readonly object stageGate = new();
    private readonly GuideGpu gpu = new();
    private readonly GuideRenderPipeline pipeline = new();
    private readonly OrderedParticle[] order = new OrderedParticle[MaximumParticles];
    private readonly OrderedSegment[] segmentOrder = new OrderedSegment[MaximumSegments];
    private readonly GuideBatch[] batches = new GuideBatch[1];
    private GuideVertex[] vertices = [];
    private GuideVertex[] projectedVertices = [];
    private readonly GuideVertex[] primitiveVertices = new GuideVertex[VerticesPerParticle];
    private readonly TrailGroundVertex[] projectionScratch = new TrailGroundVertex[TrailGroundPatch.MaximumOutputVertices];
    private readonly PreparedPrimitive[] prepared = new PreparedPrimitive[MaximumParticles + MaximumSegments];
    private readonly TrailGroundPatch groundPatch = new(TrailGround.Raycast);
    private GuideUiStage? stage;
    private bool disposed, faulted, ready, loggedSubmission;
    private int queuedParticles, queuedSegments;
    private long preparationStarted;
    private const double PreparationBudgetSeconds = .003;
    private bool PreparationBudgetSpent => Stopwatch.GetElapsedTime(preparationStarted).TotalSeconds >= PreparationBudgetSeconds;
    private string status = "No trail geometry queued.";
    private int acceptedParticles, projectionFailures, preparationStops, firstFailedTriangle, preparedVertexCount;
    private string? firstProjectionFailure;
    private float largestRadius, highestOpacity;
    private Vector3 firstParticlePosition;
    private long diagnosticAt;
    private int diagnosticMode = -1;
    private bool diagnosticPrepared;
    private int screenParticles, outsideParticles, unprojectedParticles;
    private Vector2 diagnosticViewport, firstParticleScreen, brightestScreen, brightestCorePixels, largestCorePixels;
    private float screenHighestOpacity;
    private Matrix4x4 frameProjection;
    private readonly record struct FailureGeometry(Vector3 A, Vector3 B, Vector3 C, double Area,
        int SourceVertices, float MinW, float MaxW, int FrontVertices, Vector2 ScreenMin, Vector2 ScreenMax);
    private FailureGeometry firstFailureGeometry;

    public string Status { get { lock (gate) return status; } }
    public bool Available { get { lock (gate) return !disposed && !faulted && loggedSubmission; } }

    /// <summary>Call before visibility/route early returns every UI draw.</summary>
    public void Clear()
    {
        lock (gate)
        {
            ready = false;
            queuedParticles = 0;
            queuedSegments = 0;
        }
    }

    /// <summary>
    /// Consumes at most 256 particles and 127 ribbon segments for one native draw. Empty or
    /// invalid input hides the previous frame. No hooks or GPU resources are
    /// acquired until at least one valid, nearby particle is queued.
    /// </summary>
    public void Queue(ReadOnlySpan<FairyTrailParticle> particles, Vector4 color,
        ReadOnlySpan<NativeTrailSegment> segments = default, Vector2 viewport = default, uint territory = 0, double time = 0)
    {
        lock (stageGate)
        {
            lock (gate)
            {
                ready = false;
                queuedParticles = 0;
                queuedSegments = 0;
                if (disposed || faulted) return;
                try
                {
                    acceptedParticles = projectionFailures = preparationStops = preparedVertexCount = 0;
                    firstFailedTriangle = -1; firstProjectionFailure = null;
                    firstFailureGeometry = default;
                    largestRadius = highestOpacity = screenHighestOpacity = 0; firstParticlePosition = default;
                    screenParticles = outsideParticles = unprojectedParticles = 0;
                    diagnosticViewport = brightestCorePixels = largestCorePixels = default;
                    firstParticleScreen = brightestScreen = new(float.NaN);
                    groundPatch.Begin(territory, time);
                    var preparedOk = Prepare(particles, segments, color, viewport);
                    TracePreparation(particles, segments, preparedOk);
                    if (!preparedOk) return;
                    ready = true;
                    status = loggedSubmission ? "Scene rendering active." : "Waiting for native UI boundary.";
                }
                catch (Exception ex)
                {
                    Fail(ex, "Trail preparation stopped.");
                    return;
                }
            }

            try
            {
                stage ??= new GuideUiStage(interop, log, Render, () => pi.UiBuilder.DeviceHandle);
                stage.FindShaders();
                if (stage.Faulted)
                {
                    lock (gate) { faulted = true; ready = false; status = stage.Status; }
                    return;
                }
                stage.TryDrawUiFallback();
                stage.NewFrame();
            }
            catch (Exception ex)
            {
                lock (gate) Fail(ex, "Trail native renderer unavailable.");
            }
        }
    }

    // Caller holds gate. Retained arrays are bounded by the shared GPU capacity;
    // no vertex/particle array is allocated on a steady-state frame.
    private unsafe bool Prepare(ReadOnlySpan<FairyTrailParticle> particles, ReadOnlySpan<NativeTrailSegment> segments,
        Vector4 color, Vector2 viewport)
    {
        if ((particles.IsEmpty && segments.IsEmpty) || !Finite(color) || color.W <= 0) return false;
        color = Vector4.Clamp(color, Vector4.Zero, Vector4.One);
        var manager = CameraManager.Instance();
        var camera = manager == null ? null : manager->GetActiveCamera();
        if (camera == null) { status = "Waiting for the game camera."; return false; }
        Vector3 eye = camera->SceneCamera.Position;
        if (!Finite(eye)) return false;
        var control = Control.Instance();
        Matrix4x4 projection = control == null ? default : control->ViewProjectionMatrix;
        frameProjection = projection;
        if (viewport.X <= 0 || viewport.Y <= 0)
        {
            var device = Device.Instance();
            if (device != null && device->SwapChain != null && device->SwapChain->BackBuffer != null)
                viewport = new(device->SwapChain->BackBuffer->ActualWidth, device->SwapChain->BackBuffer->ActualHeight);
        }
        diagnosticViewport = viewport;

        var count = 0;
        for (var i = 0; i < Math.Min(particles.Length, MaximumParticles); ++i)
        {
            var particle = particles[i];
            if (!Finite(particle.Position) || !Finite(particle.GroundNormal) || !float.IsFinite(particle.Radius) || particle.Radius <= 0
                || !float.IsFinite(particle.Opacity) || particle.Opacity <= 0) continue;
            var distanceSquared = Vector3.DistanceSquared(eye, particle.Position);
            if (!float.IsFinite(distanceSquared) || distanceSquared < .25f * .25f
                || distanceSquared > MaximumDistance * MaximumDistance) continue;
            // A malformed radius must not become a screen-filling translucent
            // layer. Also limit angular size for particles very near the eye.
            var radius = Math.Min(particle.Radius, Math.Min(MaximumRadius, MathF.Sqrt(distanceSquared) * .1f));
            if (particle.PixelRadius > 0)
            {
                if (!TrailGeometry.TryFrame(particle.Position, eye, particle.GroundNormal, out var right, out _, out _)) continue;
                radius = Math.Min(TrailGeometry.RadiusForPixels(particle.Position, right, particle.PixelRadius, projection, viewport),
                    Math.Min(MaximumRadius, MathF.Sqrt(distanceSquared) * .1f));
            }
            if (radius < .001f) continue;
            particle = particle with { Radius = radius, Opacity = Math.Clamp(particle.Opacity, 0, 1) };
            if (count == 0) firstParticlePosition = particle.Position;
            largestRadius = Math.Max(largestRadius, radius); highestOpacity = Math.Max(highestOpacity, particle.Opacity);
            MeasureParticle(particle, eye, projection, viewport, count == 0);
            order[count++] = new(particle, distanceSquared, i);
        }
        var segmentCount = 0;
        for (var i = 0; i < Math.Min(segments.Length, MaximumSegments); i++)
        {
            var segment = segments[i];
            if (!Finite(segment.A) || !Finite(segment.B) || !Finite(segment.GroundNormalA) || !Finite(segment.GroundNormalB)
                || !float.IsFinite(segment.WidthPixels) || segment.WidthPixels <= 0
                || !float.IsFinite(segment.Opacity) || segment.Opacity <= 0 || !float.IsFinite(segment.Glow)
                || !float.IsFinite(segment.Pulse) || Vector3.DistanceSquared(segment.A, segment.B) > 4) continue;
            var distance = Vector3.DistanceSquared(eye, (segment.A + segment.B) * .5f);
            if (!float.IsFinite(distance) || distance > MaximumDistance * MaximumDistance) continue;
            segmentOrder[segmentCount++] = new(segment, distance, i);
        }
        acceptedParticles = count;
        if (count == 0 && segmentCount == 0) return false;
        // Contact discovery runs in route order (the nearest beads first), before
        // compositing order is selected. Camera depth must not starve nearby floor queries.
        if (vertices.Length == 0)
        {
            vertices = new GuideVertex[MaximumVertices];
            projectedVertices = new GuideVertex[MaximumVertices];
        }
        preparationStarted = Stopwatch.GetTimestamp();
        var written = 0; var primitiveCount = 0; var nextParticle = 0;
        var particleBudget = segmentCount > 0 ? PreparationBudgetSeconds * .5 : PreparationBudgetSeconds;
        for (; nextParticle < count && Stopwatch.GetElapsedTime(preparationStarted).TotalSeconds < particleBudget; nextParticle++)
        {
            var sourceCount = 0;
            WriteParticle(order[nextParticle].Particle, eye, color, ref sourceCount);
            if (AppendPrimitive(sourceCount, order[nextParticle].Particle.GroundNormal != Vector3.Zero,
                order[nextParticle].DistanceSquared, ref written, ref primitiveCount)) queuedParticles++;
        }
        for (var i = 0; i < segmentCount && !PreparationBudgetSpent; i++)
        {
            var sourceCount = 0;
            WriteSegment(segmentOrder[i].Segment, eye, color, projection, viewport, ref sourceCount);
            if (AppendPrimitive(sourceCount, segmentOrder[i].Segment.GroundNormalA != Vector3.Zero,
                segmentOrder[i].DistanceSquared, ref written, ref primitiveCount)) queuedSegments++;
        }
        // Lines receive a guaranteed share of preparation; any spare time then
        // finishes farther particles without dropping work already prepared.
        for (; nextParticle < count && !PreparationBudgetSpent; nextParticle++)
        {
            var sourceCount = 0;
            WriteParticle(order[nextParticle].Particle, eye, color, ref sourceCount);
            if (AppendPrimitive(sourceCount, order[nextParticle].Particle.GroundNormal != Vector3.Zero,
                order[nextParticle].DistanceSquared, ref written, ref primitiveCount)) queuedParticles++;
        }
        if (written == 0) return false;
        Array.Sort(prepared, 0, primitiveCount, PrimitiveComparer.Instance);
        var destination = 0;
        for (var i = 0; i < primitiveCount; i++)
        {
            var primitive = prepared[i];
            Array.Copy(projectedVertices, primitive.Start, vertices, destination, primitive.Count);
            destination += primitive.Count;
        }
        batches[0] = new(0, written, 0, -2, Unlit: true, Transparent: true);
        preparedVertexCount = written;
        return true;
    }

    private bool AppendPrimitive(int sourceCount, bool ground, float distance, ref int written, ref int primitiveCount)
    {
        if (sourceCount == 0) return false;
        var start = written;
        if (ground) groundPatch.BeginPrimitive();
        for (var i = 0; i < sourceCount; i += 3)
        {
            if (PreparationBudgetSpent) { preparationStops++; written = start; return false; }
            if (!ground)
            {
                if (written + 3 > MaximumVertices) { written = start; return false; }
                Array.Copy(primitiveVertices, i, projectedVertices, written, 3); written += 3;
                continue;
            }
            var a = primitiveVertices[i]; var b = primitiveVertices[i + 1]; var c = primitiveVertices[i + 2];
            if (!groundPatch.TryProject(new(a.Position, a.Color), new(b.Position, b.Color), new(c.Position, c.Color),
                projectionScratch, out var n))
            {
                projectionFailures++;
                if (firstProjectionFailure is null)
                {
                    firstProjectionFailure = groundPatch.LastFailure ?? "empty projection"; firstFailedTriangle = i / 3;
                    CaptureFailureGeometry(sourceCount, i);
                }
                written = start; return false;
            }
            if (written + n > MaximumVertices) { preparationStops++; written = start; return false; }
            for (var j = 0; j < n; j++)
                projectedVertices[written++] = new(projectionScratch[j].Position, Vector3.UnitY, projectionScratch[j].Color, Vector2.Zero);
        }
        if (ground && !groundPatch.CompletePrimitive())
        {
            projectionFailures++;
            if (firstProjectionFailure is null)
            {
                firstProjectionFailure = groundPatch.LastFailure; firstFailedTriangle = -1;
                CaptureFailureGeometry(sourceCount, 0);
            }
            written = start; return false;
        }
        prepared[primitiveCount++] = new(start, written - start, distance);
        return true;
    }

    private void CaptureFailureGeometry(int count, int first)
    {
        var a = primitiveVertices[first].Position; var b = primitiveVertices[first + 1].Position; var c = primitiveVertices[first + 2].Position;
        var area = Math.Abs(((double)b.X - a.X) * ((double)c.Z - a.Z) - ((double)b.Z - a.Z) * ((double)c.X - a.X)) * .5;
        var minW = float.PositiveInfinity; var maxW = float.NegativeInfinity; var front = 0;
        var min = new Vector2(float.PositiveInfinity); var max = new Vector2(float.NegativeInfinity);
        for (var i = 0; i < count; i++)
        {
            var clip = Vector4.Transform(new Vector4(primitiveVertices[i].Position, 1), frameProjection);
            if (!float.IsFinite(clip.W)) continue;
            minW = Math.Min(minW, clip.W); maxW = Math.Max(maxW, clip.W);
            if (clip.W <= .0001f) continue;
            var screen = new Vector2((clip.X / clip.W + 1) * diagnosticViewport.X * .5f,
                (1 - clip.Y / clip.W) * diagnosticViewport.Y * .5f);
            if (!float.IsFinite(screen.X) || !float.IsFinite(screen.Y)) continue;
            front++; min = Vector2.Min(min, screen); max = Vector2.Max(max, screen);
        }
        if (front == 0) min = max = new(float.NaN);
        firstFailureGeometry = new(a, b, c, area, count, minW, maxW, front, min, max);
    }

    private void MeasureParticle(FairyTrailParticle particle, Vector3 eye, Matrix4x4 projection, Vector2 viewport, bool first)
    {
        var basisEye = particle.GroundNormal == Vector3.Zero ? eye : particle.Position + Vector3.UnitZ;
        if (!TrailGeometry.TryFrame(particle.Position, basisEye, particle.GroundNormal, out var right, out var up, out _))
        { unprojectedParticles++; return; }
        var position = particle.GroundNormal == Vector3.Zero
            ? TrailGeometry.AboveAltitude(particle.Position, right, up, particle.Radius) : particle.Position;
        if (!TrailGeometry.TryProjectedDisc(position, right, up, particle.Radius * .16f, projection, viewport,
            out var screen, out var corePixels)) { unprojectedParticles++; return; }
        if (first) firstParticleScreen = screen;
        if (screen.X < 0 || screen.Y < 0 || screen.X >= viewport.X || screen.Y >= viewport.Y)
        { outsideParticles++; return; }
        screenParticles++;
        largestCorePixels = Vector2.Max(largestCorePixels, corePixels);
        if (particle.Opacity > screenHighestOpacity)
        { screenHighestOpacity = particle.Opacity; brightestScreen = screen; brightestCorePixels = corePixels; }
    }

    private void TracePreparation(ReadOnlySpan<FairyTrailParticle> particles, ReadOnlySpan<NativeTrailSegment> segments, bool preparedOk)
    {
        var hasGround = false; var hasOverhead = false;
        foreach (var particle in particles)
        { hasGround |= particle.GroundNormal != Vector3.Zero; hasOverhead |= particle.GroundNormal == Vector3.Zero; }
        foreach (var segment in segments)
        { hasGround |= segment.GroundNormalA != Vector3.Zero; hasOverhead |= segment.GroundNormalA == Vector3.Zero; }
        var mode = (hasGround ? 1 : 0) | (hasOverhead ? 2 : 0);
        var elapsed = diagnosticAt == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(diagnosticAt).TotalSeconds;
        if (elapsed < 20 && !(mode != diagnosticMode && elapsed >= 1) && !(preparedOk != diagnosticPrepared && elapsed >= 5)) return;
        diagnosticAt = Stopwatch.GetTimestamp(); diagnosticMode = mode; diagnosticPrepared = preparedOk;
        log.Information("Wayfinder trail preparation: mode={Mode}, particles input/accepted/prepared={Input}/{Accepted}/{Prepared}, segments={Segments}, vertices={Vertices}, projectionFailures={Failures} first={Reason} triangle={Triangle}, budgetStops={Stops}, rays={Rays}, faces={Faces}, misses={Misses}, invalidHits={Invalid}, radiusMax={Radius}, opacityMax={Opacity}, firstPosition={Position}, GPU submissions={Submissions}; viewport={Viewport}, screen/outside/unprojected={OnScreen}/{Outside}/{Unprojected}, firstScreen={FirstScreen}, brightestScreen={BrightScreen}, brightestCoreDiameterPx={BrightCore}, largestCoreDiameterPx={LargestCore}, screenOpacityMax={ScreenOpacity}.",
            mode, particles.Length, acceptedParticles, queuedParticles, queuedSegments, preparedVertexCount,
            projectionFailures, firstProjectionFailure ?? "none", firstFailedTriangle, preparationStops,
            groundPatch.Queries, groundPatch.FaceCount, groundPatch.MissedQueries, groundPatch.InvalidFaceHits,
            largestRadius, highestOpacity, firstParticlePosition, gpu.Submissions, diagnosticViewport,
            screenParticles, outsideParticles, unprojectedParticles, firstParticleScreen, brightestScreen,
            brightestCorePixels, largestCorePixels, screenHighestOpacity);
        if (firstProjectionFailure is not null)
        {
            var failure = firstFailureGeometry;
            log.Information("Wayfinder trail first rejected primitive: kind={Kind}, reason={Reason}, triangle={Triangle}, vertices={Count}, source={A}/{B}/{C}, XZarea={Area}, clipW={MinW}..{MaxW}, frontVertices={Front}, projectedBounds={Min}/{Max}; whole primitive rolled back before GPU submission.",
                failure.SourceVertices == VerticesPerParticle ? "particle" : "line", firstProjectionFailure, firstFailedTriangle,
                failure.SourceVertices, failure.A, failure.B, failure.C, failure.Area, failure.MinW, failure.MaxW,
                failure.FrontVertices, failure.ScreenMin, failure.ScreenMax);
        }
    }

    private sealed class PrimitiveComparer : IComparer<PreparedPrimitive>
    {
        internal static readonly PrimitiveComparer Instance = new();
        public int Compare(PreparedPrimitive a, PreparedPrimitive b)
        {
            var depth = b.DistanceSquared.CompareTo(a.DistanceSquared);
            return depth != 0 ? depth : a.Start.CompareTo(b.Start);
        }
    }

    private void WriteParticle(FairyTrailParticle particle, Vector3 eye, Vector4 tint, ref int written)
    {
        // A round ground glow has no camera-facing orientation. Keep its world
        // tessellation stable while the camera moves so confirmed geometry is reusable.
        var basisEye = particle.GroundNormal == Vector3.Zero ? eye : particle.Position + Vector3.UnitZ;
        if (!TrailGeometry.TryFrame(particle.Position, basisEye, particle.GroundNormal, out var right, out var up, out var normal)) return;
        // Ground discs start on the measured tangent plane and are then split
        // across the actual neighboring collision faces; overhead billboards
        // include their full outer glow above the requested altitude.
        var position = particle.GroundNormal == Vector3.Zero
            ? TrailGeometry.AboveAltitude(particle.Position, right, up, particle.Radius) : particle.Position;
        var rgb = new Vector3(tint.X, tint.Y, tint.Z);
        var alpha = tint.W * particle.Opacity;
        var coreColor = new Vector4(Vector3.Lerp(rgb, Vector3.One, .6f), alpha);
        var innerColor = new Vector4(Vector3.Lerp(rgb, Vector3.One, .25f), alpha * .65f);
        var middleColor = new Vector4(rgb, alpha * .14f);
        var outerColor = new Vector4(rgb, 0);
        var center = new GuideVertex(position, normal, coreColor, Vector2.Zero);
        for (var i = 0; i < Sectors; ++i)
        {
            var a = (right * Directions[i].X + up * Directions[i].Y) * particle.Radius;
            var next = (i + 1) % Sectors;
            var b = (right * Directions[next].X + up * Directions[next].Y) * particle.Radius;
            var innerA = new GuideVertex(position + a * .16f, normal, innerColor, Vector2.Zero);
            var innerB = new GuideVertex(position + b * .16f, normal, innerColor, Vector2.Zero);
            var middleA = new GuideVertex(position + a * .48f, normal, middleColor, Vector2.Zero);
            var middleB = new GuideVertex(position + b * .48f, normal, middleColor, Vector2.Zero);
            var outerA = new GuideVertex(position + a, normal, outerColor, Vector2.Zero);
            var outerB = new GuideVertex(position + b, normal, outerColor, Vector2.Zero);
            primitiveVertices[written++] = center; primitiveVertices[written++] = innerA; primitiveVertices[written++] = innerB;
            primitiveVertices[written++] = innerA; primitiveVertices[written++] = middleA; primitiveVertices[written++] = middleB;
            primitiveVertices[written++] = innerA; primitiveVertices[written++] = middleB; primitiveVertices[written++] = innerB;
            primitiveVertices[written++] = middleA; primitiveVertices[written++] = outerA; primitiveVertices[written++] = outerB;
            primitiveVertices[written++] = middleA; primitiveVertices[written++] = outerB; primitiveVertices[written++] = middleB;
        }
    }

    private void WriteSegment(NativeTrailSegment segment, Vector3 eye, Vector4 tint, Matrix4x4 projection,
        Vector2 viewport, ref int written)
    {
        var along = segment.B - segment.A;
        if (!TrailGeometry.TryRibbonSide(segment.A, along, eye, segment.GroundNormalA, out var sideA)
            || !TrailGeometry.TryRibbonSide(segment.B, along, eye, segment.GroundNormalB, out var sideB)) return;
        var radiusA = TrailGeometry.RadiusForPixels(segment.A, sideA, segment.WidthPixels * 3.5f, projection, viewport);
        var radiusB = TrailGeometry.RadiusForPixels(segment.B, sideB, segment.WidthPixels * 3.5f, projection, viewport);
        if (radiusA <= 0 || radiusB <= 0) return;
        var a = segment.GroundNormalA == Vector3.Zero
            ? TrailGeometry.AboveAltitude(segment.A, sideA, Vector3.Zero, radiusA) : segment.A;
        var b = segment.GroundNormalB == Vector3.Zero
            ? TrailGeometry.AboveAltitude(segment.B, sideB, Vector3.Zero, radiusB) : segment.B;
        var rgb = new Vector3(tint.X, tint.Y, tint.Z);
        var strength = Math.Clamp(segment.Opacity, 0, 1) * tint.W;
        Quad(1, new(rgb, strength * .045f * Math.Clamp(segment.Glow, 0, 2)), ref written);
        Quad(.5f, new(rgb, strength * .13f * Math.Clamp(segment.Glow, 0, 2)), ref written);
        Quad(1 / 7f, new(rgb, strength), ref written);
        Quad(Math.Max(.7f, segment.WidthPixels * .4f) / (segment.WidthPixels * 7),
            new(Vector3.Lerp(rgb, new(1, .98f, .93f), .5f), strength * .7f * Math.Clamp(segment.Pulse, 0, 1)), ref written);

        void Quad(float scale, Vector4 color, ref int index)
        {
            if (color.W <= .001f) return;
            var normal = Vector3.UnitY; // The shared shader's unlit branch ignores lighting normals.
            var a0 = new GuideVertex(a - sideA * (radiusA * scale), normal, color, Vector2.Zero);
            var a1 = new GuideVertex(a + sideA * (radiusA * scale), normal, color, Vector2.Zero);
            var b0 = new GuideVertex(b - sideB * (radiusB * scale), normal, color, Vector2.Zero);
            var b1 = new GuideVertex(b + sideB * (radiusB * scale), normal, color, Vector2.Zero);
            primitiveVertices[index++] = a0; primitiveVertices[index++] = b0; primitiveVertices[index++] = b1;
            primitiveVertices[index++] = a0; primitiveVertices[index++] = b1; primitiveVertices[index++] = a1;
        }
    }

    private unsafe void Render(nint context)
    {
        lock (gate)
        {
            if (disposed || faulted || !ready) return;
            ready = false; // Never replay a stale queue if UI production stops.
            try
            {
                var device = Device.Instance();
                if (device == null || device->SwapChain == null || device->SwapChain->BackBuffer == null) return;
                var before = gpu.Submissions;
                pipeline.TryDraw(context, (nint)device->SwapChain->BackBuffer->D3D11Texture2D,
                    nativeDevice => gpu.Draw(nativeDevice, vertices.AsSpan(0, batches[0].Count), batches, 1));
                if (gpu.Submissions > before && !loggedSubmission)
                {
                    loggedSubmission = true;
                    log.Information("Wayfinder trail native GPU submission: {Particles} particles, {Segments} line segments, {Triangles} triangles; scene depth enabled, pre-native-UI stage.",
                        queuedParticles, queuedSegments, batches[0].Count / 3);
                }
            }
            catch (Exception ex) { Fail(ex, "Trail native renderer stopped."); }
        }
    }

    // Caller holds gate; faults fail closed rather than falling back to ImGui.
    private void Fail(Exception ex, string message)
    {
        faulted = true;
        ready = false;
        status = message;
        log.Error(ex, "XivWayfinder: {Message}", message);
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static Vector2[] MakeDirections()
    {
        var result = new Vector2[Sectors];
        for (var i = 0; i < result.Length; ++i)
        {
            var angle = i * MathF.Tau / Sectors;
            result[i] = new(MathF.Cos(angle), MathF.Sin(angle));
        }
        return result;
    }

    public void Dispose()
    {
        lock (stageGate)
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                ready = false;
                queuedParticles = 0;
                queuedSegments = 0;
            }
            // No gate held while retiring a hook: its in-flight callback must
            // be able to acquire gate and finish before disposal can complete.
            var retiring = stage;
            stage = null;
            try { retiring?.Dispose(); }
            finally
            {
                lock (gate)
                {
                    try { gpu.Dispose(); }
                    finally { pipeline.Dispose(); vertices = []; projectedVertices = []; }
                }
            }
        }
    }
}
