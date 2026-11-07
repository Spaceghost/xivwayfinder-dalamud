using System.Diagnostics;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

internal sealed record TerrainTrailPreview(Scene Scene, Vector3[] Path, float ProgressArc, Vector3 Focus);

/// <summary>Temporary development-only evidence from real nearby collision and a query-only route.</summary>
internal sealed class TrailTerrainCheck : IDisposable
{
    private const int RaysPerFrame = 4;
    private const double ScanSeconds = .001, Lifetime = 75;
    private readonly bool isDev;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Navmesh navmesh;
    private readonly TrailSurfaceSampler floors;
    private readonly List<(int X, int Z)> cells = [];
    private readonly Dictionary<(int X, int Z), TrailSurfaceHit> sampled = [];
    private readonly List<(float Arc, Vector3 Position, Vector3 Normal)> routeSamples = [];
    private readonly Queue<(TrailSurfaceHit Lower, TrailSurfaceHit Upper)> curbPairs = [];
    private readonly record struct Riser(Vector3 A, Vector3 B, Vector3 C, int LowerHeight, int UpperHeight);
    private readonly HashSet<Riser> examinedRisers = [];
    private (TrailSurfaceHit Lower, TrailSurfaceHit Upper) checkingPair;
    private TerrainTrailPreview? preview;
    private Vector3 origin, goal, measuredGoal;
    private uint territory;
    private string mode = "", phase = "off";
    private bool refining;
    private int cursor, rays, candidates, validateIndex, validateCount;
    private int visibleCells, floorHits, pairsExamined, bevels, riserMisses, invalidRisers, steepRisers, duplicateRisers, routeAttempts;
    private double expires, requestedAt;
    private long started, expiresTick;
    private Vector3[] route = [];
    private float routeLength;

    public TrailTerrainCheck(IDalamudPluginInterface pi, IGameGui gameGui, IPluginLog log)
    {
        isDev = pi.IsDev; this.gameGui = gameGui; this.log = log;
        navmesh = new(pi, log);
        floors = new(Cast);
    }

    public string Command(string argument, uint zone, Vector3? player, double now)
    {
        if (!isDev) return "Terrain checks are available only in a development plugin.";
        var kind = argument.Trim().ToLowerInvariant();
        if (kind == "off") { Stop("command"); return "Temporary terrain check stopped."; }
        if (kind is not ("curb" or "slope")) return "Use /wayfinder terraincheck curb|slope|off.";
        if (player is not { } at || zone == 0 || !TrailPlacement.IsFinite(at) || !double.IsFinite(now))
            return "A terrain check needs a logged-in character in a loaded zone.";
        Stop("replaced");
        origin = at; territory = zone; mode = kind; phase = "scanning";
        expires = now + Lifetime; expiresTick = Stopwatch.GetTimestamp() + (long)(Lifetime * Stopwatch.Frequency);
        cells.Clear(); sampled.Clear(); routeSamples.Clear(); curbPairs.Clear(); examinedRisers.Clear(); cursor = candidates = 0;
        visibleCells = floorHits = pairsExamined = bevels = riserMisses = invalidRisers = steepRisers = duplicateRisers = routeAttempts = 0;
        refining = false; cells.AddRange(TerrainCheckGrid.Coarse);
        log.Information("Wayfinder terrain check started: kind={Kind}, zone={Zone}, player={Player}; temporary visual only, expires in 75 seconds, collision cap 4 rays/frame and 1 ms.", mode, territory, origin);
        return $"Scanning visible nearby ground for a {mode}; temporary trail only, expires in 75 seconds or when you move.";
    }

    public void Update(uint zone, Vector3? player, bool visible, double now)
    {
        if (!isDev || phase == "off") return;
        try { UpdateCore(zone, player, visible, now); }
        catch (Exception ex)
        {
            Stop("collision or route check failed");
            log.Warning(ex, "Wayfinder temporary terrain check stopped; normal guidance is unchanged.");
        }
    }

    private void UpdateCore(uint zone, Vector3? player, bool visible, double now)
    {
        if (zone != territory || player is not { } at || !visible || !double.IsFinite(now)
            || now >= expires || !TrailPlacement.IsFinite(at) || Vector3.DistanceSquared(at, origin) > .01f)
        { Stop("expired, hidden, moved or changed zone"); return; }
        if (phase == "ready") return;
        rays = 0; started = Stopwatch.GetTimestamp();
        if (phase == "scanning") Scan(now);
        if (phase == "riser" && CanCast) CheckRiser(now);
        if (phase is "extend" or "fallback" && CanCast)
        {
            var endpointPhase = phase;
            var hit = Cast(goal + Vector3.UnitY * .3f, -Vector3.UnitY, 1.8f);
            var tolerance = mode == "curb" ? .06f : .45f;
            var reason = hit is not { } floor ? "no collision hit"
                : !Walkable(floor) ? "hit is not a walkable face"
                : Math.Abs(floor.Point.Y - goal.Y) > tolerance ? "hit left the measured height"
                : !OnScreen(floor.Point) ? "hit is outside the current view" : null;
            if (reason is not null)
            {
                log.Information("Wayfinder terrain endpoint: phase={Phase}, requested={Requested}, hit={Hit}, reason={Reason}, measuredFallback={Fallback}.",
                    endpointPhase, goal, hit is { } observed ? observed.Point.ToString() : "none", reason, measuredGoal);
                if (endpointPhase == "extend")
                {
                    // A narrow curb cap is a valid destination. Reconfirm its
                    // measured point next frame instead of demanding a platform
                    // beyond it; path-crossing validation remains unchanged.
                    goal = measuredGoal; phase = "fallback"; return;
                }
                RejectCandidate("measured endpoint unavailable: " + reason); return;
            }
            goal = hit!.Value.Point; phase = "routing"; requestedAt = now; routeAttempts++;
        }
        if (phase == "routing")
        {
            navmesh.Aim(zone, origin, goal, true, now);
            if (navmesh.Following)
            {
                route = navmesh.Path; routeLength = PathTrail.Length(route);
                if (!float.IsFinite(routeLength) || routeLength < 1 || routeLength > 30)
                { RejectCandidate("confirmed route is too short or detours more than 30 yalms"); return; }
                routeSamples.Clear(); validateIndex = 0; validateCount = (int)MathF.Ceiling(routeLength / .125f) + 1;
                phase = "validating";
            }
            else if (now - requestedAt > 8) { RejectCandidate("path query: " + navmesh.Status); return; }
        }
        if (phase == "validating") Validate(now);
    }

    public TerrainTrailPreview? GetPreview(uint zone, Vector3 player)
        => isDev && phase == "ready" && zone == territory && Stopwatch.GetTimestamp() < expiresTick
            && TrailPlacement.IsFinite(player) && Vector3.DistanceSquared(player, origin) <= .01f ? preview : null;

    private void Scan(double now)
    {
        if (curbPairs.TryDequeue(out checkingPair)) { phase = "riser"; return; }
        for (var inspected = 0; cursor < cells.Count && CanCast && inspected < 64; inspected++)
        {
            var cell = cells[cursor++];
            var point = origin + new Vector3(cell.X * TerrainCheckGrid.Spacing, 0, cell.Z * TerrainCheckGrid.Spacing);
            if (!OnScreen(point)) continue;
            visibleCells++;
            var hit = Cast(point + Vector3.UnitY * 1.55f, -Vector3.UnitY, 3.1f);
            if (hit is not { } surface || !Walkable(surface) || Math.Abs(surface.Point.Y - origin.Y) > 1.25f || !OnScreen(surface.Point)) continue;
            floorHits++;
            sampled[cell] = surface;
            var normal = TrailPlacement.SurfaceNormal(surface.Normal, surface.A, surface.B, surface.C);
            if (mode == "slope" && normal.Y is > .55f and < .985f)
            {
                var uphill = Vector2.Normalize(new(-normal.X, -normal.Z));
                var offset = new Vector3(uphill.X, 0, uphill.Y) * .75f;
                goal = surface.Point + offset;
                goal.Y -= (normal.X * offset.X + normal.Z * offset.Z) / normal.Y;
                Select(surface, surface, now); return;
            }
            if (mode != "curb" || normal.Y < .92f) continue;
            var step = refining ? 1 : 2;
            foreach (var neighbor in new[] { (cell.X - step, cell.Z), (cell.X + step, cell.Z), (cell.X, cell.Z - step), (cell.X, cell.Z + step) })
            {
                if (!sampled.TryGetValue(neighbor, out var other) || TrailPlacement.SurfaceNormal(other.Normal, other.A, other.B, other.C).Y < .92f) continue;
                var difference = Math.Abs(surface.Point.Y - other.Point.Y);
                if (difference is < .08f or > .5f) continue;
                var upper = surface.Point.Y > other.Point.Y ? surface : other;
                var lower = surface.Point.Y > other.Point.Y ? other : surface;
                curbPairs.Enqueue((lower, upper));
            }
            // Preserve every neighboring pair from this cell. Each pair is
            // encountered once, when its second endpoint enters sampled.
            if (curbPairs.TryDequeue(out checkingPair)) { phase = "riser"; return; }
        }
        if (cursor < cells.Count) return;
        if (mode == "curb" && !refining)
        {
            // A narrow tread can lie entirely between half-yalm grid points.
            // Keep all measured coarse cells and fill the gaps once; discovery
            // still requires a real riser and the same visible route crossing.
            refining = true; cells.AddRange(TerrainCheckGrid.Refinement);
            log.Information("Wayfinder terrain refinement started: coarseCells={Coarse}, additionalCells={Additional}, totalCells={Total}, spacing={Spacing}; unchanged collision budget and expiry.",
                TerrainCheckGrid.Coarse.Count, TerrainCheckGrid.Refinement.Count, cells.Count, TerrainCheckGrid.Spacing);
            return;
        }
        Stop("no qualifying visible " + mode + " with a confirmed route found");
    }

    private void CheckRiser(double now)
    {
        var (lower, upper) = checkingPair;
        var direction = upper.Point - lower.Point; direction.Y = 0;
        var length = direction.Length();
        if (!float.IsFinite(length) || length < .01f) { invalidRisers++; phase = "scanning"; return; }
        direction /= length;
        var from = lower.Point with { Y = (lower.Point.Y + upper.Point.Y) * .5f };
        pairsExamined++;
        var hit = Cast(from, direction, length);
        if (hit is not { } riser) { riserMisses++; phase = "scanning"; return; }
        var delta = riser.Point - from;
        var along = Vector3.Dot(delta, direction);
        var normal = Vector3.Cross(riser.B - riser.A, riser.C - riser.A);
        var normalLength = normal.Length();
        if (!TrailPlacement.IsFinite(riser.Point) || !TrailPlacement.IsFinite(riser.A)
            || !TrailPlacement.IsFinite(riser.B) || !TrailPlacement.IsFinite(riser.C)
            || !float.IsFinite(normalLength) || normalLength < .00001f
            || !float.IsFinite(along) || along < -.001f || along > length + .001f
            || (delta - direction * along).LengthSquared() > .0001f)
        { invalidRisers++; phase = "scanning"; return; }
        normal /= normalLength;
        // Flat endpoints can straddle a bevel. Demand actual steep collision
        // between them before spending one of the six independent route checks.
        if (Math.Abs(normal.Y) > .2f) { bevels++; phase = "scanning"; return; }
        if (Math.Abs(Vector3.Dot(normal, direction)) < .1f
            || Math.Abs(Vector3.Dot(riser.Point - riser.A, normal)) > .01f || !OnScreen(riser.Point))
        { invalidRisers++; phase = "scanning"; return; }
        var a = riser.A; var b = riser.B; var c = riser.C;
        if (Compare(a, b) > 0) (a, b) = (b, a);
        if (Compare(b, c) > 0) (b, c) = (c, b);
        if (Compare(a, b) > 0) (a, b) = (b, a);
        var key = new Riser(a, b, c, (int)MathF.Round(lower.Point.Y * 100), (int)MathF.Round(upper.Point.Y * 100));
        if (!examinedRisers.Add(key)) { duplicateRisers++; phase = "scanning"; return; }
        steepRisers++;
        log.Information("Wayfinder steep riser measured: point={Point}, normal={Normal}, span={Lower}/{Upper}, triangle={A}/{B}/{C}.",
            riser.Point, normal, lower.Point, upper.Point, riser.A, riser.B, riser.C);
        goal = upper.Point + direction * .1f;
        Select(lower, upper, now);
    }

    private static int Compare(Vector3 a, Vector3 b)
    {
        var x = a.X.CompareTo(b.X); if (x != 0) return x;
        var y = a.Y.CompareTo(b.Y); return y != 0 ? y : a.Z.CompareTo(b.Z);
    }

    private void Select(TrailSurfaceHit lower, TrailSurfaceHit upper, double now)
    {
        if (++candidates > 6) { Stop("six nearby candidates did not yield a verified route"); return; }
        measuredGoal = upper.Point;
        phase = "extend"; requestedAt = now;
        log.Information("Wayfinder terrain candidate: kind={Kind}, lower={Lower}, upper={Upper}, deltaY={Delta}, lowerTriangle={A}/{B}/{C}, upperTriangle={D}/{E}/{F}.",
            mode, lower.Point, upper.Point, upper.Point.Y - lower.Point.Y, lower.A, lower.B, lower.C, upper.A, upper.B, upper.C);
    }

    private void Validate(double now)
    {
        floors.Begin(territory);
        while (validateIndex < validateCount && rays <= RaysPerFrame - 2 && CanCast)
        {
            var arc = Math.Min(routeLength, validateIndex * .125f);
            var point = PathTrail.At(route, arc);
            if (!floors.TryPlace(point, now, out var placed, out var normal))
            { RejectCandidate("confirmed path has an unavailable collision sample"); return; }
            routeSamples.Add((arc, placed, normal)); validateIndex++;
        }
        if (validateIndex < validateCount) return;
        var focus = -1; var minY = float.PositiveInfinity; var maxY = float.NegativeInfinity; var minNormalY = 1f; var maxStep = 0f;
        for (var i = 0; i < routeSamples.Count; i++)
        {
            var sample = routeSamples[i]; minY = Math.Min(minY, sample.Position.Y); maxY = Math.Max(maxY, sample.Position.Y);
            minNormalY = Math.Min(minNormalY, sample.Normal.Y);
            if (i == 0) continue;
            var previous = routeSamples[i - 1]; var difference = Math.Abs(sample.Position.Y - previous.Position.Y);
            maxStep = Math.Max(maxStep, difference);
            if (!OnScreen(sample.Position) || !OnScreen(previous.Position)) continue;
            if (mode == "curb" && difference is >= .06f and <= .6f && sample.Normal.Y >= .92f && previous.Normal.Y >= .92f) focus = i;
            if (mode == "slope" && sample.Normal.Y is > .55f and < .985f && previous.Normal.Y is > .55f and < .985f && difference > .002f) focus = i;
        }
        if (focus < 1 || maxY - minY < .06f) { RejectCandidate("walkable path did not cross a visible " + mode); return; }
        var target = new Target(TargetSource.Explicit, territory, goal.X, goal.Z, goal.Y, "Development terrain check: " + mode);
        var guide = new Guide(GuideKind.Walk, target, routeLength, Heading.DirectionTo(origin, goal), null);
        var scene = new Scene(true, guide, origin, 0, PathTrail.LookAhead(route, 0, 4), "Local terrain check", TerritoryId: territory);
        preview = new(scene, route, 0, (routeSamples[focus - 1].Position + routeSamples[focus].Position) * .5f);
        phase = "ready";
        log.Information("Wayfinder terrain check ready: kind={Kind}, zone={Zone}, corners={Corners}, length={Length}, samples={Samples}, floorY={MinY}..{MaxY}, minNormalY={NormalY}, maxAdjacentRise={Step}, visibleCrossing={Before}/{After}, candidate={Goal}; same configured trail renderer, no player movement or destination edit.",
            mode, territory, route.Length, routeLength, routeSamples.Count, minY - TrailPlacement.GroundClearance,
            maxY - TrailPlacement.GroundClearance, minNormalY, maxStep, routeSamples[focus - 1].Position, routeSamples[focus].Position, goal);
    }

    private bool OnScreen(Vector3 point) => gameGui.WorldToScreen(point + Vector3.UnitY * .16f, out _, out var inside) && inside;
    private static bool Walkable(TrailSurfaceHit hit) => TrailPlacement.IsFinite(hit.Point)
        && TrailPlacement.SurfaceNormal(hit.Normal, hit.A, hit.B, hit.C).Y > .55f;
    private bool CanCast => rays < RaysPerFrame && Stopwatch.GetElapsedTime(started).TotalSeconds < ScanSeconds;
    private TrailSurfaceHit? Cast(Vector3 point, Vector3 direction, float distance)
    {
        if (rays >= RaysPerFrame) return null;
        rays++;
        return TrailGround.Raycast(point, direction, distance);
    }
    private void RejectCandidate(string reason)
    {
        navmesh.Idle(); preview = null; route = []; phase = "scanning";
        log.Information("Wayfinder terrain candidate skipped: {Reason}.", reason);
    }
    private void Stop(string reason)
    {
        if (phase != "off") log.Information("Wayfinder terrain check stopped: {Reason}. kind={Kind}, examinedCells={Cells}/{TotalCells}, coarseCells={Coarse}/{TotalCoarse}, refinedCells={Refined}/{TotalRefined}, visibleCells={Visible}, floorHits={Floors}, examinedPairs={Pairs}, bevels={Bevels}, riserMisses={Misses}, invalidRisers={Invalid}, uniqueSteepRisers={Steep}, duplicateRisers={Duplicates}, selectedCandidates={Selected}, routeAttempts={Routes}.",
            reason, mode, cursor, cells.Count, Math.Min(cursor, TerrainCheckGrid.Coarse.Count), TerrainCheckGrid.Coarse.Count,
            Math.Max(0, cursor - TerrainCheckGrid.Coarse.Count), refining ? TerrainCheckGrid.Refinement.Count : 0,
            visibleCells, floorHits, pairsExamined, bevels, riserMisses, invalidRisers, steepRisers, duplicateRisers, Math.Min(candidates, 6), routeAttempts);
        preview = null; phase = "off"; route = []; curbPairs.Clear(); navmesh.Idle();
    }
    public void Dispose() { Stop("unloaded"); navmesh.Dispose(); }
}
