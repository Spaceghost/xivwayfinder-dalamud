using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

/// <summary>
/// Framework-thread, read-only collision evidence for a short own-route hop.
/// Typed SceneWrapper.Raycast retains the game's quadtree broadphase. Unlike
/// the Vector3-only module convenience sweep, RaycastParams explicitly carries
/// the sphere radius in Origin.W. No collision pointers survive the shared lock.
/// </summary>
internal sealed unsafe class GuideJumpProbe
{
    private const int QueryBudget = GuideJumpEvidenceProgress.SceneQueryBudget;
    private const double SecondsBudget = .0015;
    private const int TrajectorySegments = GuideJumpEvidenceProgress.TrajectorySegments;
    private GuideJumpRequest? request;
    private GuideJumpPlan? candidate;
    private readonly GuideJumpFloor[] floors = new GuideJumpFloor[GuideJumpRequest.FloorSampleCount];
    private readonly GuideJumpFloorTriangle[] floorTriangles = new GuideJumpFloorTriangle[GuideJumpRequest.FloorSampleCount];
    private readonly GuideJumpEvidenceProgress collection = new();
    private bool forwardChecked;
    private uint territory;
    private int floorIndex, supportIndex, sweepIndex, freshBodyIndex;
    private double started;
    private double criticalStarted = double.NaN, approvedAt = double.NaN;
    private GuideJumpPlan? landingPlan;
    private int landingSupportIndex;
    private double landingStarted = double.NaN, landingVerified = double.NaN;
    private GuideJumpProbeResult? finished;
    public string Status { get; private set; } = "No own-route obstacle evaluated.";

    public void Reset()
    {
        request = null; candidate = null; finished = null;
        floorIndex = supportIndex = sweepIndex = freshBodyIndex = 0;
        criticalStarted = approvedAt = double.NaN;
        landingPlan = null; landingSupportIndex = 0; landingStarted = landingVerified = double.NaN;
        collection.Reset(); forwardChecked = false;
        territory = 0;
    }

    public GuideJumpProbeResult Probe(uint zone, Vector3[] route, float ownArc, int direction,
        GuideJumpCapability capability, double now)
    {
        if (zone == 0 || !double.IsFinite(now) || !GuideJumpPolicy.TryRequest(route, ownArc, direction, capability, out var next))
        { Reset(); return Result(GuideJumpEvidence.Unknown, null, "No verified jump capability or straight route leg."); }
        if (request is null || zone != territory || !ReferenceEquals(request.Route, route)
            || request.Capability != capability || Math.Abs(request.StartArc - ownArc) > .0001f
            || Math.Sign(request.EndArc - request.StartArc) != direction || !request.MatchesRoute()
            || now < started)
        {
            Reset(); request = next; territory = zone; started = now; collection.Begin(now);
        }
        if (finished is { } cached)
        {
            if (cached.Evidence != GuideJumpEvidence.Approved || GuideJumpEvidenceProgress.Fresh(approvedAt, now)
                && GuideJumpEvidenceProgress.Fresh(criticalStarted, now)) return cached;
            collection.Stop(); return Finish(GuideJumpEvidence.Unknown, "Takeoff approval expired; fresh geometry is required.");
        }
        if (collection.Expired(now) && !collection.Stopped)
        {
            // Under heavy query pressure, repeatedly resetting a Pending
            // request could hold the companion forever. Decline this exact
            // request without trusting stale evidence; normal placement may
            // proceed and a different owned arc can start a new assessment.
            collection.Stop();
            return Finish(GuideJumpEvidence.Unknown, "Collision proof exceeded its bounded freshness window; hop declined.");
        }
        if (!CollisionSession.TryOpen(out var collision)) return Result(candidate is null ? collection.Incomplete : GuideJumpEvidence.Pending,
            null, "Collision scene is loading or busy.");
        try
        {
            while (floorIndex < floors.Length)
            {
                if (floorIndex == 1 && !forwardChecked)
                {
                    var floor = floors[0];
                    if (floor.Evidence == GuideJumpEvidence.Clear && floor.NormalY >= .55f
                        && Math.Abs(floor.Position.Y - request!.Start.Y) <= .7f)
                    {
                        var from = new Vector3(request!.Start.X, floor.Position.Y + .08f, request.Start.Z);
                        var to = new Vector3(request.End.X, from.Y + request.End.Y - request.Start.Y, request.End.Z);
                        var forwardEvidence = collision.Trace(from, to, 0, false, out var forwardHit);
                        if (forwardEvidence == GuideJumpEvidence.Unknown)
                            return Result(collection.Incomplete, null, "Waiting for bounded obstacle evidence.");
                        collection.MadeProgress(now);
                        if (forwardEvidence == GuideJumpEvidence.Blocked)
                            collection.ObserveForwardBlocker(request, floor, forwardHit.Point,
                                TrailPlacement.SurfaceNormal(forwardHit.Normal, forwardHit.V1, forwardHit.V2, forwardHit.V3));
                    }
                    forwardChecked = true;
                }
                var point = request!.Point(floorIndex);
                var evidence = collision.Trace(point + Vector3.UnitY * .75f, point - Vector3.UnitY * .75f, 0, false, out var hit);
                if (evidence == GuideJumpEvidence.Unknown) return Result(collection.Incomplete, null, "Collecting bounded ground evidence.");
                collection.MadeProgress(now);
                floorTriangles[floorIndex] = evidence == GuideJumpEvidence.Blocked ? new(hit.V1, hit.V2, hit.V3) : default;
                floors[floorIndex++] = evidence == GuideJumpEvidence.Blocked
                    ? new(GuideJumpEvidence.Clear, hit.Point, TrailPlacement.SurfaceNormal(hit.Normal, hit.V1, hit.V2, hit.V3).Y)
                    : new(GuideJumpEvidence.Blocked, default, 0);
            }
            if (candidate is null)
            {
                var geometry = GuideJumpPolicy.Evaluate(request!, floors);
                if (geometry.Plan is null) { finished = geometry; Status = geometry.Reason; return geometry; }
                candidate = geometry.Plan;
                if (!GuideJumpFloorCoverage.Covers(candidate, floorTriangles))
                    return Finish(GuideJumpEvidence.Unknown, "Actual floor triangles do not cover the complete hop segment.");
            }
            var levels = BodyLevels(capability);
            while (sweepIndex < TrajectorySegments * levels)
            {
                var segment = sweepIndex / levels; var level = sweepIndex % levels;
                var from = candidate.Sample(segment / (float)TrajectorySegments).FeetPosition;
                var to = candidate.Sample((segment + 1f) / TrajectorySegments).FeetPosition;
                var flightDelta = candidate.FlightProgress((segment + 1f) / TrajectorySegments)
                    - candidate.FlightProgress(segment / (float)TrajectorySegments);
                var evidence = BodySlice(ref collision, from, to, capability, level, levels, .015f + candidate.Apex * flightDelta * flightDelta);
                if (evidence == GuideJumpEvidence.Unknown) return Result(GuideJumpEvidence.Pending, null, "Collecting bounded body clearance.");
                if (evidence != GuideJumpEvidence.Clear) return Finish(GuideJumpEvidence.Blocked, "Jump body or head clearance is obstructed.");
                collection.MadeProgress(now);
                sweepIndex++;
            }
            // Bulk work may span multiple bounded updates. Critical endpoint
            // support and takeoff body proof are collected LAST and must all
            // remain within a short freshness window before approval.
            if (!double.IsNaN(criticalStarted) && !GuideJumpEvidenceProgress.Fresh(criticalStarted, now))
                return Finish(GuideJumpEvidence.Unknown, "Critical takeoff/landing evidence could not be refreshed in time.");
            const int footprint = GuideJumpEvidenceProgress.FootprintSamples;
            while (supportIndex < footprint * 2)
            {
                var foot = supportIndex < footprint ? candidate.Takeoff : candidate.Landing;
                var evidence = Support(ref collision, foot, capability.Radius, supportIndex % footprint);
                if (evidence == GuideJumpEvidence.Unknown) return Result(GuideJumpEvidence.Pending, null, "Refreshing takeoff and landing support.");
                if (evidence != GuideJumpEvidence.Clear) return Finish(GuideJumpEvidence.Blocked, "Takeoff or landing support changed.");
                if (double.IsNaN(criticalStarted)) criticalStarted = now;
                collection.MadeProgress(now); supportIndex++;
            }
            var takeoff = candidate.Sample(0).FeetPosition;
            while (freshBodyIndex < levels)
            {
                var evidence = BodySlice(ref collision, takeoff, takeoff, capability, freshBodyIndex, levels);
                if (evidence == GuideJumpEvidence.Unknown) return Result(GuideJumpEvidence.Pending, null, "Refreshing takeoff body clearance.");
                if (evidence != GuideJumpEvidence.Clear) return Finish(GuideJumpEvidence.Blocked, "Takeoff body clearance changed.");
                collection.MadeProgress(now); freshBodyIndex++;
            }
            approvedAt = now;
            return Finish(GuideJumpEvidence.Approved, "Verified low obstacle on the companion's own route.");
        }
        finally { collision.Dispose(); }
    }

    /// <summary>Recheck the proposed airborne step against dynamic collision, without advancing its clock on Unknown.</summary>
    public GuideJumpEvidence ValidateStep(GuideJumpFrame previous, GuideJumpFrame next)
    {
        if (!ReferenceEquals(previous.Plan, next.Plan) || next.Plan is not { Valid: true }
            || !float.IsFinite(previous.Progress) || !float.IsFinite(next.Progress)
            || previous.Progress is < 0 or > 1 || next.Progress is < 0 or > 1
            || previous != previous.Plan.Sample(previous.Progress) || next != next.Plan.Sample(next.Progress)
            || next.Progress < previous.Progress || next.Progress - previous.Progress > .25f
            || !CollisionSession.TryOpen(out var collision)) return GuideJumpEvidence.Unknown;
        try
        {
            var capability = next.Plan.Request.Capability; var levels = BodyLevels(capability);
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (next.Plan.FlightProgress(next.Progress) >= .7f)
            {
                if (!ReferenceEquals(landingPlan, next.Plan))
                { landingPlan = next.Plan; landingSupportIndex = 0; landingStarted = landingVerified = double.NaN; }
                if (!GuideJumpEvidenceProgress.Fresh(landingVerified, now) || !GuideJumpEvidenceProgress.Fresh(landingStarted, now))
                {
                    if (!GuideJumpEvidenceProgress.Fresh(landingStarted, now))
                    { landingSupportIndex = 0; landingStarted = now; landingVerified = double.NaN; }
                    while (landingSupportIndex < GuideJumpEvidenceProgress.FootprintSamples)
                    {
                        var evidence = Support(ref collision, next.Plan.Landing, capability.Radius, landingSupportIndex);
                        if (evidence != GuideJumpEvidence.Clear) return evidence;
                        landingSupportIndex++;
                    }
                    landingVerified = now;
                }
                // Reserve one ray in the body budget: the actual touchdown
                // center must still exist in THIS update, not only in a cache.
                if (next.Plan.FlightProgress(next.Progress) >= .99f)
                {
                    var evidence = Support(ref collision, next.Plan.Landing, capability.Radius, 0);
                    if (evidence != GuideJumpEvidence.Clear) return evidence;
                }
            }
            for (var level = 0; level < levels; ++level)
            {
                // Runtime slow-frame steps may span up to a quarter clip. The
                // parabola rises above its chord by apex * deltaProgress^2.
                var progress = next.Plan.FlightProgress(next.Progress) - previous.Plan.FlightProgress(previous.Progress);
                var margin = .015f + next.Plan.Apex * progress * progress;
                var evidence = BodySlice(ref collision, previous.FeetPosition, next.FeetPosition, capability, level, levels, margin);
                if (evidence != GuideJumpEvidence.Clear) return evidence;
            }
            return GuideJumpEvidence.Clear;
        }
        finally { collision.Dispose(); }
    }

    // Seven slices plus one fresh touchdown ray fit the four-scene budget.
    // Wider enclosing spheres still cover every interval between slices.
    private static int BodyLevels(GuideJumpCapability capability) => Math.Clamp((int)MathF.Ceiling(capability.Height / capability.Radius) + 1, 2, GuideJumpEvidenceProgress.MaximumBodySlices);
    private static GuideJumpEvidence Support(ref CollisionSession collision, Vector3 foot, float radius, int index)
    {
        var offset = index switch { 1 => Vector3.UnitX, 2 => -Vector3.UnitX, 3 => Vector3.UnitZ, 4 => -Vector3.UnitZ, _ => Vector3.Zero };
        var at = foot + offset * radius;
        var evidence = collision.Trace(at + Vector3.UnitY * .15f, at - Vector3.UnitY * .15f, 0, false, out var hit);
        if (evidence == GuideJumpEvidence.Unknown) return evidence;
        return evidence == GuideJumpEvidence.Blocked && Math.Abs(hit.Point.Y - foot.Y) <= .015f
            && TrailPlacement.SurfaceNormal(hit.Normal, hit.V1, hit.V2, hit.V3).Y >= .55f
            ? GuideJumpEvidence.Clear : GuideJumpEvidence.Blocked;
    }
    private static GuideJumpEvidence BodySlice(ref CollisionSession collision, Vector3 from, Vector3 to,
        GuideJumpCapability capability, int level, int levels, float margin = .015f)
    {
        var separation = capability.Height / (levels - 1);
        // Overlapping spheres enclose the full measured cylinder, including the
        // gaps between sample heights. Extra margin bounds parabola/chord error.
        var radius = MathF.Sqrt(capability.Radius * capability.Radius + separation * separation * .25f) + margin;
        var offset = Vector3.UnitY * (level * separation);
        // Only the bottom overlap may ignore the already-profiled walkable
        // floor. Head/body sweeps must still see a thin floor above the model,
        // even if that mesh exposes only its upward-facing triangle normal.
        var ignoreFloor = level != levels - 1 && offset.Y < radius;
        return collision.Trace(from + offset, to + offset, radius, ignoreFloor, out _);
    }

    private GuideJumpProbeResult Result(GuideJumpEvidence evidence, GuideJumpPlan? plan, string reason)
    { Status = reason; return new(evidence, plan, reason); }
    private GuideJumpProbeResult Finish(GuideJumpEvidence evidence, string reason)
    {
        var result = Result(evidence, evidence == GuideJumpEvidence.Approved ? candidate : null, reason);
        finished = result; return result;
    }

    private struct CollisionSession : IDisposable
    {
        private BGCollisionModule* module;
        private long began;
        private int queries;
        public static bool TryOpen(out CollisionSession session)
        {
            session = default;
            if (!OperatingSystem.IsWindows()) return false;
            var framework = Framework.Instance();
            var module = framework == null ? null : framework->BGCollisionModule;
            if (module == null || module->ShuttingDown || module->LoadInProgressCounter > 0
                || !TryAcquireSRWLockShared(&module->UpdateTaskLock)) return false;
            if (module->ShuttingDown || module->LoadInProgressCounter > 0 || module->SceneManager == null
                || module->SceneManager->FirstScene == null || module->SceneManager->NumScenes is < 1 or > 4)
            { ReleaseSRWLockShared(&module->UpdateTaskLock); return false; }
            session.module = module; session.began = Stopwatch.GetTimestamp(); return true;
        }

        public GuideJumpEvidence Trace(Vector3 from, Vector3 to, float radius, bool ignoreWalkable, out RaycastHit nearest)
        {
            nearest = default;
            if (module == null || queries >= QueryBudget || Stopwatch.GetElapsedTime(began).TotalSeconds >= SecondsBudget
                || !TrailPlacement.IsFinite(from) || !TrailPlacement.IsFinite(to) || !float.IsFinite(radius) || radius < 0)
                return GuideJumpEvidence.Unknown;
            var delta = to - from; var distance = delta.Length();
            if (distance < .00001f) { delta = Vector3.UnitY * .00001f; distance = .00001f; }
            var direction = delta / distance;
            var origin = new Vector4(from, radius);
            var filter = new RaycastMaterialFilter { Mask = 0x4000, Value = 0x4000 };
            var parameters = new RaycastParams { Origin = &origin, Direction = &direction, MaxDistance = &distance,
                Algorithm = 4 | (radius > 0 ? 1 : 0) | (ignoreWalkable ? 2 : 0), MaxPlaneNormalY = .55f, MaterialFilter = &filter };
            var found = false; var count = 0;
            for (var scene = module->SceneManager->FirstScene; scene != null; scene = (SceneWrapper*)scene->Next)
            {
                if (++count > 4 || queries++ >= QueryBudget || Stopwatch.GetElapsedTime(began).TotalSeconds >= SecondsBudget
                    || scene->Scene == null || scene->VirtualTable == null || scene->HasLoadingColliders()) return GuideJumpEvidence.Unknown;
                RaycastHit hit = default;
                if (!scene->Raycast(&hit, 1, &parameters)) continue;
                if (!float.IsFinite(hit.Distance) || !TrailPlacement.IsFinite(hit.Point)) return GuideJumpEvidence.Unknown;
                if (!found || hit.Distance < nearest.Distance) { nearest = hit; found = true; }
            }
            return count == module->SceneManager->NumScenes
                ? found ? GuideJumpEvidence.Blocked : GuideJumpEvidence.Clear : GuideJumpEvidence.Unknown;
        }
        public void Dispose()
        { if (module != null) { ReleaseSRWLockShared(&module->UpdateTaskLock); module = null; } }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool TryAcquireSRWLockShared(nint* srwLock);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void ReleaseSRWLockShared(nint* srwLock);
}
