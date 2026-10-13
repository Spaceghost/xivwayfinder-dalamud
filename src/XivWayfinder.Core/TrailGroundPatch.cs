using System.Diagnostics;
using System.Numerics;

namespace XivWayfinder.Core;

public readonly record struct TrailGroundVertex(Vector3 Position, Vector4 Color);

/// <summary>Projects trail triangles onto nearby collision faces. Stable footprint
/// witnesses discover overlapping treads; subtracting measured faces establishes
/// coverage across seams. Missing coverage postpones the primitive instead of
/// extending a tangent plane through unknown terrain. Queries stay on the caller thread.</summary>
public sealed class TrailGroundPatch(Func<Vector3, Vector3, float, TrailSurfaceHit?> raycast, Func<double>? clock = null)
{
    public const int MaximumOutputVertices = 768;
    public const int MaximumQueries = 64;
    public const double TimeBudgetSeconds = .00075;
    private const float SearchAbove = .4f, SearchBelow = .4f;
    private const float ObstructionAbove = TrailPlacement.FloorHeightSlack;
    private const int MaximumFaces = 512, MaximumPieces = 64, MaximumPolygon = 32;
    // A full default trail has hundreds of footprint witnesses. Give the bounded
    // query scheduler enough headroom to renew them without starving its far end.
    private const double Lifetime = .6;
    private const double RefreshAfter = Lifetime / 2;
    private const float ProbeSpacing = .125f, IndexSpacing = .5f;
    private readonly record struct Probe(double At, double Used);
    private readonly Dictionary<(int X, int Z, int Y), Probe> probes = [];
    private readonly List<((int X, int Z, int Y) Key, double At)> refresh = [];
    private readonly Dictionary<(int X, int Z), List<int>> spatial = [];
    private readonly List<int> largeFaces = [], nearby = [];
    private readonly List<int> contradictedFaces = [];
    private readonly int[] seen = new int[MaximumFaces];
    private int stamp;
    private readonly record struct TriangleKey(Vector3 A, Vector3 B, Vector3 C);
    private readonly record struct ProjectedCorner(Vector3 Position, float B, float C);
    private readonly record struct Projection(double At, ulong Revision, bool HigherLayer, ProjectedCorner[] Vertices);
    private readonly Dictionary<TriangleKey, Projection> projections = [];
    private double oldestWitness;
    private ulong faceRevision;
    private bool collectingPrimitive, primitiveUnderHigherLayer, primitiveWithoutHigherLayer;
    private readonly Func<double> queryClock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    private readonly List<Face> faces = [];
    private readonly List<int> candidates = [];
    private readonly List<int> higherCandidates = [];
    private readonly Vector2[] piecesA = new Vector2[MaximumPieces * MaximumPolygon], piecesB = new Vector2[MaximumPieces * MaximumPolygon];
    private readonly int[] sizesA = new int[MaximumPieces], sizesB = new int[MaximumPieces];
    private uint territory;
    private double now, querySeconds;
    public int Queries { get; private set; }
    public int FaceCount => faces.Count;
    public int MissedQueries { get; private set; }
    public int InvalidFaceHits { get; private set; }
    public string? LastFailure { get; private set; }
    private bool Reject(string reason) { LastFailure = reason; return false; }

    public void BeginPrimitive()
    {
        collectingPrimitive = true;
        primitiveUnderHigherLayer = primitiveWithoutHigherLayer = false;
    }

    public bool CompletePrimitive()
    {
        collectingPrimitive = false;
        return !(primitiveUnderHigherLayer && primitiveWithoutHigherLayer)
            || Reject("partial higher surface over primitive");
    }

    private void ObserveLayer(bool higher)
    {
        if (!collectingPrimitive) return;
        primitiveUnderHigherLayer |= higher;
        primitiveWithoutHigherLayer |= !higher;
    }

    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, double At, Vector2 Min, Vector2 Max, ulong Revision = 0)
    {
        public float Height(Vector2 p, Vector2 origin = default) => A.Y
            - (Normal.X * (p.X - (A.X - origin.X)) + Normal.Z * (p.Y - (A.Z - origin.Y))) / Normal.Y;
    }

    public void Begin(uint zone, double time)
    {
        Queries = 0; querySeconds = 0; MissedQueries = 0; InvalidFaceHits = 0; LastFailure = null;
        collectingPrimitive = false;
        if (zone != territory || time < now || !double.IsFinite(time)) { faces.Clear(); spatial.Clear(); largeFaces.Clear(); probes.Clear(); projections.Clear(); }
        territory = zone; now = time;
        if (faces.RemoveAll(f => time - f.At >= Lifetime) > 0)
        {
            spatial.Clear(); largeFaces.Clear();
            for (var i = 0; i < faces.Count; i++) IndexFace(i);
        }
        if (probes.Count > 8192) probes.Clear();
        // Renew the oldest still-used witnesses before hard expiry. Half of the
        // ray budget remains available for newly visible route/footprint cells.
        refresh.Clear();
        foreach (var entry in probes)
            if (time - entry.Value.At >= RefreshAfter && time - entry.Value.Used < Lifetime)
                refresh.Add((entry.Key, entry.Value.At));
        refresh.Sort(static (a, b) => a.At.CompareTo(b.At));
        foreach (var entry in refresh)
        {
            if (Queries >= MaximumQueries / 2 || !CanQuery || faces.Count >= MaximumFaces) break;
            SampleProbe(entry.Key, probes[entry.Key].Used);
        }
    }

    /// <summary>Returns complete triangulated coverage, or no output. Every emitted
    /// triangle lies inside a measured floor face. Subtracting covered regions also
    /// ensures cached lower decks cannot create a second copy beneath the route.</summary>
    public bool TryProject(TrailGroundVertex a, TrailGroundVertex b, TrailGroundVertex c,
        Span<TrailGroundVertex> output, out int written)
    {
        written = 0; LastFailure = null;
        if (!double.IsFinite(now) || !PositionValid(a.Position) || !PositionValid(b.Position)
            || !PositionValid(c.Position) || !Finite(a.Color) || !Finite(b.Color) || !Finite(c.Color)) return Reject("invalid source");
        var key = new TriangleKey(a.Position, b.Position, c.Position);
        if (projections.TryGetValue(key, out var cached) && now >= cached.At && now - cached.At < Lifetime
            && ProjectionStillSupported(key, cached.Revision))
        {
            if (cached.Revision != faceRevision) projections[key] = cached with { Revision = faceRevision };
            if (output.Length < cached.Vertices.Length) return Reject("cached output capacity");
            foreach (var vertex in cached.Vertices)
                output[written++] = new(vertex.Position, a.Color * (1 - vertex.B - vertex.C) + b.Color * vertex.B + c.Color * vertex.C);
            ObserveLayer(cached.HigherLayer);
            return true;
        }
        // Clip near the primitive origin so small floor seams remain precise even
        // at large zone coordinates. World-space float lerps create false cracks.
        var origin = Xz(a.Position);
        Span<Vector2> footprint = stackalloc Vector2[3] { Vector2.Zero, Xz(b.Position) - origin, Xz(c.Position) - origin };
        if (Math.Abs(Area(footprint)) < 1e-9f) return Reject("degenerate footprint");
        var sourceNormal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
        if (Math.Abs(sourceNormal.Y) < 1e-10f) return Reject("vertical footprint");
        float Expected(Vector2 p) => a.Position.Y - TrailPlacement.GroundClearance
            - (sourceNormal.X * p.X + sourceNormal.Z * p.Y) / sourceNormal.Y;
        var lo = Vector2.Min(footprint[0], Vector2.Min(footprint[1], footprint[2]));
        var hi = Vector2.Max(footprint[0], Vector2.Max(footprint[1], footprint[2]));
        Span<Vector2> overlap = stackalloc Vector2[MaximumPolygon];
        Span<Vector2> scratch = stackalloc Vector2[MaximumPolygon];
        Span<Vector2> facePoints = stackalloc Vector2[3];
        // A lower collision face can extend under another tread or prop. Face
        // coverage alone cannot discover it. Stable footprint lattice witnesses
        // inspect the height envelope before using any cached face as coverage.
        if (!Discover(footprint, origin, a.Position.Y - TrailPlacement.GroundClearance, sourceNormal)) return Reject("footprint witnesses unavailable");
        for (var attempt = 0; attempt <= MaximumQueries; attempt++)
        {
            candidates.Clear(); higherCandidates.Clear(); var containingFace = -1;
            Nearby(lo + origin, hi + origin);
            foreach (var i in nearby)
            {
                var f = faces[i];
                var floorLo = f.Min - origin;
                var floorHi = f.Max - origin;
                if (floorHi.X < lo.X || floorLo.X > hi.X || floorHi.Y < lo.Y || floorLo.Y > hi.Y) continue;
                footprint.CopyTo(overlap); var n = 3;
                facePoints[0] = Xz(f.A) - origin; facePoints[1] = Xz(f.B) - origin; facePoints[2] = Xz(f.C) - origin;
                var sign = MathF.Sign(Area(facePoints));
                for (var edge = 0; edge < 3 && n >= 3; edge++)
                {
                    n = Clip(overlap[..n], facePoints[edge], facePoints[(edge + 1) % 3], sign, scratch);
                    if (n < 0) return Reject("polygon capacity");
                    scratch[..n].CopyTo(overlap);
                }
                if (n < 3 || Math.Abs(Area(overlap[..n])) <= 1e-9f) continue;
                // Use the actual overlap, never a distant extrapolation of the face.
                var eligible = true; var above = false; var entirelyAbove = true;
                foreach (var point in overlap[..n])
                {
                    var difference = f.Height(point, origin) - Expected(point);
                    above |= difference > SearchAbove + .001f;
                    entirelyAbove &= difference > SearchAbove + .001f;
                    if (difference > SearchAbove + .001f || difference < -SearchBelow) eligible = false;
                }
                if (above)
                {
                    // Do not quietly use a lower face when a slope leaves the
                    // route's support envelope inside the same footprint.
                    if (!entirelyAbove) return Reject("floor leaves route height envelope");
                    if (higherCandidates.Count == 64) return Reject("higher candidate capacity");
                    higherCandidates.Add(i);
                }
                if (!eligible) continue;
                if (candidates.Count == 64) return Reject("candidate capacity");
                candidates.Add(i);
                if (n == 3 && overlap[0] == footprint[0] && overlap[1] == footprint[1] && overlap[2] == footprint[2]) containingFace = i;
            }
            // A complete higher layer may be a separate deck above this route.
            // Partial high coverage could instead be a tall tread or prop. Its
            // layer is ambiguous, so postpone the whole primitive rather than
            // drawing the confirmed lower face through that obstruction.
            if (higherCandidates.Count > 0 && !Partition(footprint, a, b, c, origin,
                    output, out _, out var higherMissing, higherCandidates, true))
            {
                // A missing neighboring triangle can look like the edge of a
                // deck. Measure that exact uncovered region before classifying
                // it as a partial tall obstruction.
                if (!float.IsFinite(higherMissing.X) || !CanQuery || faces.Count >= MaximumFaces)
                    return Reject("higher surface coverage budget");
                var expected = Expected(higherMissing);
                var upperSample = Sample(new(higherMissing.X + origin.X, expected + ObstructionAbove, higherMissing.Y + origin.Y),
                    ObstructionAbove + SearchBelow);
                if (upperSample is not { } upper || upper.Point.Y <= expected + SearchAbove + .001f
                    || !TryFace(upper, now, out var upperFace) || faces.Any(f => SameTriangle(f, upperFace)))
                    return Reject("partial higher surface over route");
                AddFace(upperFace);
                continue;
            }
            // Most triangles in a dot are wholly inside one floor face. Preserve
            // their exact vertices/colors without allocating a polygon partition.
            if (candidates.Count == 1 && containingFace == candidates[0])
            {
                if (output.Length < 3) return Reject("output capacity");
                var floor = faces[containingFace];
                output[0] = a with { Position = a.Position with { Y = floor.Height(footprint[0], origin) + TrailPlacement.GroundClearance } };
                output[1] = b with { Position = b.Position with { Y = floor.Height(footprint[1], origin) + TrailPlacement.GroundClearance } };
                output[2] = c with { Position = c.Position with { Y = floor.Height(footprint[2], origin) + TrailPlacement.GroundClearance } };
                written = 3; Remember(key, output[..written], Math.Min(oldestWitness, floor.At)); return true;
            }
            // Match the downward ray's first-surface rule, with deterministic ties.
            var originCenter = Centroid(footprint);
            for (var i = 1; i < candidates.Count; i++)
            {
                var index = candidates[i]; var j = i;
                while (j > 0 && faces[candidates[j - 1]].Height(originCenter, origin) < faces[index].Height(originCenter, origin))
                { candidates[j] = candidates[j - 1]; j--; }
                candidates[j] = index;
            }
            // A single order is valid only when overlapping planes keep that
            // order throughout their actual shared footprint. Intersecting decks
            // are ambiguous; postpone instead of projecting onto the lower face.
            for (var left = 0; left < candidates.Count; left++)
                for (var right = left + 1; right < candidates.Count; right++)
                {
                    var first = faces[candidates[left]]; var second = faces[candidates[right]];
                    footprint.CopyTo(overlap); var n = 3;
                    for (var faceIndex = 0; faceIndex < 2 && n >= 3; faceIndex++)
                    {
                        var comparisonFace = faceIndex == 0 ? first : second;
                        facePoints[0] = Xz(comparisonFace.A) - origin; facePoints[1] = Xz(comparisonFace.B) - origin; facePoints[2] = Xz(comparisonFace.C) - origin;
                        var sign = MathF.Sign(Area(facePoints));
                        for (var edge = 0; edge < 3 && n >= 3; edge++)
                        {
                            n = Clip(overlap[..n], facePoints[edge], facePoints[(edge + 1) % 3], sign, scratch);
                            if (n < 0) return Reject("polygon capacity");
                            scratch[..n].CopyTo(overlap);
                        }
                    }
                    if (n < 3 || Math.Abs(Area(overlap[..n])) <= 1e-8f) continue;
                    foreach (var point in overlap[..n])
                        if (first.Height(point, origin) < second.Height(point, origin) - .00001f) return Reject("ambiguous overlapping floors");
                }
            if (Partition(footprint, a, b, c, origin, output, out written, out var missing))
            {
                var at = oldestWitness;
                foreach (var index in candidates) at = Math.Min(at, faces[index].At);
                if (written > 0) Remember(key, output[..written], at);
                return written > 0;
            }
            written = 0;
            if (!float.IsFinite(missing.X)) return Reject("partition capacity");
            if (Queries >= MaximumQueries || Queries >= 4 && querySeconds >= TimeBudgetSeconds || faces.Count >= MaximumFaces)
                return Reject("uncovered footprint budget");
            var hit = Sample(new(missing.X + origin.X, Expected(missing) + SearchAbove, missing.Y + origin.Y), SearchAbove + SearchBelow);
            if (hit is not { } found) return Reject("no collision at uncovered footprint");
            if (!TryFace(found, now, out var face)) { InvalidFaceHits++; return Reject("invalid collision triangle"); }
            if (faces.Any(f => SameTriangle(f, face))) return Reject("collision seam made no progress");
            AddFace(face);
        }
        return Reject("projection attempt capacity");
    }

    // Probe whole lattice cells touched by the footprint, including its edge.
    // Camera-dependent width changes reuse the same world probes. The finite
    // spacing cannot prove absence of a smaller prop entirely between witnesses;
    // scene depth remains the final occlusion check for such geometry.
    private bool Discover(ReadOnlySpan<Vector2> triangle, Vector2 origin, float height, Vector3 normal)
    {
        oldestWitness = now;
        var min = Vector2.Min(triangle[0], Vector2.Min(triangle[1], triangle[2])) + origin;
        var max = Vector2.Max(triangle[0], Vector2.Max(triangle[1], triangle[2])) + origin;
        var x0 = (int)MathF.Floor(min.X / ProbeSpacing); var x1 = (int)MathF.Floor(max.X / ProbeSpacing);
        var z0 = (int)MathF.Floor(min.Y / ProbeSpacing); var z1 = (int)MathF.Floor(max.Y / ProbeSpacing);
        if (x1 - x0 > 32 || z1 - z0 > 32) return false;
        Span<Vector2> overlap = stackalloc Vector2[MaximumPolygon];
        Span<Vector2> scratch = stackalloc Vector2[MaximumPolygon];
        Span<Vector2> cell = stackalloc Vector2[4];
        for (var x = x0; x <= x1; x++) for (var z = z0; z <= z1; z++)
        {
            var world = new Vector2((x + .5f) * ProbeSpacing, (z + .5f) * ProbeSpacing);
            var local = world - origin;
            var expected = height - (normal.X * local.X + normal.Z * local.Y) / normal.Y;
            var level = (int)MathF.Ceiling((expected + SearchAbove) / ProbeSpacing);
            var key = (x, z, level);
            if (probes.TryGetValue(key, out var observation) && now >= observation.At && now - observation.At < Lifetime)
            {
                oldestWitness = Math.Min(oldestWitness, observation.At);
                probes[key] = observation with { Used = now };
                continue;
            }
            var left = x * ProbeSpacing - origin.X; var near = z * ProbeSpacing - origin.Y;
            cell[0] = new(left, near); cell[1] = new(left + ProbeSpacing, near);
            cell[2] = new(left + ProbeSpacing, near + ProbeSpacing); cell[3] = new(left, near + ProbeSpacing);
            triangle.CopyTo(overlap); var n = 3;
            for (var edge = 0; edge < 4 && n >= 3; edge++)
            {
                n = Clip(overlap[..n], cell[edge], cell[(edge + 1) % 4], 1, scratch);
                if (n < 0) return false;
                scratch[..n].CopyTo(overlap);
            }
            if (n < 3 || Math.Abs(Area(overlap[..n])) <= 1e-9f) continue;
            if (!CanQuery || faces.Count >= MaximumFaces) return false;
            if (!SampleProbe(key, now)) return false;
        }
        return true;
    }

    private bool SampleProbe((int X, int Z, int Y) key, double used)
    {
        var origin = new Vector3((key.X + .5f) * ProbeSpacing, key.Y * ProbeSpacing, (key.Z + .5f) * ProbeSpacing);
        if (HasFreshHigherFace(origin))
        {
            // The upper triangle is already measured at this exact X/Z. Renew
            // the route layer directly, instead of charging two rays for every
            // witness beneath the same deck. The first witness after the upper
            // face's refresh deadline must measure both layers again.
            Observe(Sample(origin, SearchAbove + SearchBelow + ProbeSpacing));
            probes[key] = new(now, used);
            return true;
        }
        // Start high enough to discover a tall overlapping tread even when an
        // old lower plane covers the footprint. Ordinary floors still cost one
        // ray; an intervening upper deck also needs the original near-layer ray.
        var hit = Sample(origin + Vector3.UnitY * (ObstructionAbove - SearchAbove),
            ObstructionAbove + SearchBelow + ProbeSpacing);
        Observe(hit);
        if (hit is { } upper && upper.Point.Y > origin.Y + .001f)
        {
            if (!CanQuery || faces.Count >= MaximumFaces) return false;
            Observe(Sample(origin, SearchAbove + SearchBelow + ProbeSpacing));
        }
        probes[key] = new(now, used);
        return true;
    }

    private bool HasFreshHigherFace(Vector3 origin)
    {
        var point = Xz(origin);
        Nearby(point, point);
        foreach (var index in nearby)
        {
            var face = faces[index];
            if (now - face.At >= RefreshAfter) continue;
            var height = face.Height(point);
            if (height <= origin.Y + .001f || height > origin.Y + ObstructionAbove - SearchAbove) continue;
            var a = Xz(face.A); var b = Xz(face.B); var c = Xz(face.C);
            var sign = MathF.Sign(Cross(b - a, c - a));
            if (Cross(b - a, point - a) * sign >= 0 && Cross(c - b, point - b) * sign >= 0
                && Cross(a - c, point - c) * sign >= 0) return true;
        }
        return false;
    }

    private TrailSurfaceHit? Sample(Vector3 origin, float distance)
    {
        Queries++; var started = queryClock();
        var hit = raycast(origin, -Vector3.UnitY, distance);
        querySeconds += Math.Max(0, queryClock() - started);
        if (hit is null) MissedQueries++;
        RetireContradictedSupport(origin, distance, hit);
        return hit;
    }

    private void RetireContradictedSupport(Vector3 origin, float distance, TrailSurfaceHit? hit)
    {
        if (faces.Count == 0 || !PositionValid(origin) || !float.IsFinite(distance) || distance <= 0) return;
        const float tolerance = .001f;
        var bottom = origin.Y - distance;
        if (hit is { } found)
        {
            // Only the clear segment before a valid first hit is evidence.
            // Equal-height faces and floors hidden below that hit remain valid;
            // malformed or out-of-ray results establish no clear interval.
            if (!TryFace(found, now, out _) || Math.Abs(found.Point.X - origin.X) > tolerance
                || Math.Abs(found.Point.Z - origin.Z) > tolerance
                || found.Point.Y > origin.Y || found.Point.Y < bottom) return;
            bottom = found.Point.Y;
        }
        var point = Xz(origin);
        Nearby(point, point); contradictedFaces.Clear();
        foreach (var index in nearby)
        {
            var face = faces[index];
            var height = face.Height(point);
            // Keep a small margin at both ray endpoints: an origin on a floor
            // or a hit at the far limit does not reliably disprove that floor.
            if (height >= origin.Y - tolerance || height <= bottom + tolerance) continue;
            var a = Xz(face.A); var b = Xz(face.B); var c = Xz(face.C);
            var sign = MathF.Sign(Cross(b - a, c - a));
            // Do not expand the footprint. A lattice witness outside a narrow
            // tread can miss while the cached triangle still supports the mark.
            if (Cross(b - a, point - a) * sign < 0 || Cross(c - b, point - b) * sign < 0
                || Cross(a - c, point - c) * sign < 0) continue;
            contradictedFaces.Add(index);
        }
        if (contradictedFaces.Count == 0) return;
        contradictedFaces.Sort();
        for (var i = contradictedFaces.Count - 1; i >= 0; i--) faces.RemoveAt(contradictedFaces[i]);
        spatial.Clear(); largeFaces.Clear();
        for (var i = 0; i < faces.Count; i++) IndexFace(i);
        // Removal is rare and can invalidate support anywhere on a retired
        // triangle. Rebuild retained projections from the surviving evidence;
        // keep probes (including misses) so this adds no collision-query loop.
        projections.Clear(); faceRevision++;
    }

    private void Observe(TrailSurfaceHit? hit)
    {
        if (hit is not { } found) return;
        if (!TryFace(found, now, out var face)) { InvalidFaceHits++; return; }
        var existing = faces.FindIndex(f => SameTriangle(f, face));
        if (existing >= 0) faces[existing] = face with { Revision = faces[existing].Revision };
        else AddFace(face);
    }

    private void AddFace(Face face)
    {
        faces.Add(face with { Revision = ++faceRevision });
        IndexFace(faces.Count - 1);
    }

    private bool ProjectionStillSupported(TriangleKey key, ulong revision)
    {
        if (revision == faceRevision) return true;
        var min = Vector2.Min(Xz(key.A), Vector2.Min(Xz(key.B), Xz(key.C)));
        var max = Vector2.Max(Xz(key.A), Vector2.Max(Xz(key.B), Xz(key.C)));
        Nearby(min, max);
        // A newly measured overlapping face can invalidate an otherwise fresh
        // lower-floor projection. Preserve unrelated retained geometry, and do
        // no spatial work on the normal unchanged-scene cache path.
        foreach (var index in nearby)
        {
            var face = faces[index];
            if (face.Revision > revision && face.Min.X <= max.X && face.Max.X >= min.X
                && face.Min.Y <= max.Y && face.Max.Y >= min.Y) return false;
        }
        return true;
    }

    private void Remember(TriangleKey key, ReadOnlySpan<TrailGroundVertex> projected, double at)
    {
        // Geometry stays fixed while opacity/glow colors pulse. Retain the
        // triangulation only as long as its oldest collision evidence is valid.
        if (projections.Count >= 32768) projections.Clear();
        var vertices = new ProjectedCorner[projected.Length];
        var origin = Xz(key.A); var b = Xz(key.B) - origin; var c = Xz(key.C) - origin;
        var denominator = Cross(b, c);
        for (var i = 0; i < vertices.Length; i++)
        {
            var p = Xz(projected[i].Position) - origin;
            vertices[i] = new(projected[i].Position, Cross(p, c) / denominator, Cross(b, p) / denominator);
        }
        var higher = higherCandidates.Count > 0;
        projections[key] = new(at, faceRevision, higher, vertices);
        ObserveLayer(higher);
    }

    private bool CanQuery => Queries < MaximumQueries && (Queries < 4 || querySeconds < TimeBudgetSeconds);

    private void IndexFace(int index)
    {
        var face = faces[index];
        var x0 = (int)MathF.Floor(face.Min.X / IndexSpacing); var x1 = (int)MathF.Floor(face.Max.X / IndexSpacing);
        var z0 = (int)MathF.Floor(face.Min.Y / IndexSpacing); var z1 = (int)MathF.Floor(face.Max.Y / IndexSpacing);
        if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 64) { largeFaces.Add(index); return; }
        for (var x = x0; x <= x1; x++) for (var z = z0; z <= z1; z++)
        {
            if (!spatial.TryGetValue((x, z), out var bucket)) spatial[(x, z)] = bucket = [];
            bucket.Add(index);
        }
    }

    private void Nearby(Vector2 min, Vector2 max)
    {
        nearby.Clear();
        if (++stamp == int.MaxValue) { Array.Clear(seen); stamp = 1; }
        var x0 = (int)MathF.Floor(min.X / IndexSpacing); var x1 = (int)MathF.Floor(max.X / IndexSpacing);
        var z0 = (int)MathF.Floor(min.Y / IndexSpacing); var z1 = (int)MathF.Floor(max.Y / IndexSpacing);
        for (var x = x0; x <= x1; x++) for (var z = z0; z <= z1; z++)
        {
            if (!spatial.TryGetValue((x, z), out var bucket)) continue;
            foreach (var index in bucket)
                if (seen[index] != stamp) { seen[index] = stamp; nearby.Add(index); }
        }
        nearby.AddRange(largeFaces);
    }

    // Each face consumes only the still-uncovered footprint. Result polygons are
    // convex; their fan triangles stay on that exact measured collision triangle.
    private bool Partition(ReadOnlySpan<Vector2> triangle, TrailGroundVertex a, TrailGroundVertex b, TrailGroundVertex c, Vector2 origin,
        Span<TrailGroundVertex> output, out int written, out Vector2 missing,
        List<int>? selection = null, bool coverageOnly = false)
    {
        written = 0; missing = new(float.NaN);
        triangle.CopyTo(piecesA); sizesA[0] = 3;
        var input = piecesA; var destination = piecesB; var sizes = sizesA; var nextSizes = sizesB; var pieces = 1;
        Span<Vector2> work = stackalloc Vector2[MaximumPolygon];
        Span<Vector2> next = stackalloc Vector2[MaximumPolygon];
        Span<Vector2> cutter = stackalloc Vector2[3];
        var denominator = Cross(triangle[1] - triangle[0], triangle[2] - triangle[0]);
        foreach (var faceIndex in selection ?? candidates)
        {
            var face = faces[faceIndex];
            cutter[0] = Xz(face.A) - origin; cutter[1] = Xz(face.B) - origin; cutter[2] = Xz(face.C) - origin;
            var sign = MathF.Sign(Area(cutter));
            var newCount = 0;
            for (var piece = 0; piece < pieces; piece++)
            {
                var count = sizes[piece]; input.AsSpan(piece * MaximumPolygon, count).CopyTo(work);
                for (var edge = 0; edge < 3 && count >= 3; edge++)
                {
                    var n = Clip(work[..count], cutter[edge], cutter[(edge + 1) % 3], -sign, next);
                    if (n < 0) return false;
                    if (n >= 3 && Math.Abs(Area(next[..n])) > 1e-8f)
                    {
                        if (newCount >= MaximumPieces) return false;
                        next[..n].CopyTo(destination.AsSpan(newCount * MaximumPolygon)); nextSizes[newCount++] = n;
                    }
                    count = Clip(work[..count], cutter[edge], cutter[(edge + 1) % 3], sign, next);
                    if (count < 0) return false;
                    next[..count].CopyTo(work);
                }
                for (var i = 1; !coverageOnly && i + 1 < count; i++)
                {
                    if (Math.Abs(Cross(work[i] - work[0], work[i + 1] - work[0])) <= 1e-9f) continue;
                    if (written + 3 > output.Length) return false;
                    for (var corner = 0; corner < 3; corner++)
                    {
                        var p = work[corner == 0 ? 0 : corner == 1 ? i : i + 1];
                        var wb = Cross(p - triangle[0], triangle[2] - triangle[0]) / denominator;
                        var wc = Cross(triangle[1] - triangle[0], p - triangle[0]) / denominator;
                        var color = a.Color * (1 - wb - wc) + b.Color * wb + c.Color * wc;
                        output[written++] = new(new(p.X + origin.X, face.Height(p, origin) + TrailPlacement.GroundClearance, p.Y + origin.Y), color);
                    }
                }
            }
            (input, destination) = (destination, input); (sizes, nextSizes) = (nextSizes, sizes); pieces = newCount;
            if (pieces == 0) return true;
        }
        var largest = -1f;
        for (var i = 0; i < pieces; i++)
        {
            var polygon = input.AsSpan(i * MaximumPolygon, sizes[i]); var area = Math.Abs(Area(polygon));
            if (area <= largest) continue;
            largest = area; missing = Centroid(polygon);
        }
        return false;
    }

    private static int Clip(ReadOnlySpan<Vector2> polygon, Vector2 a, Vector2 b, float sign, Span<Vector2> output)
    {
        var n = 0; if (polygon.IsEmpty) return n;
        if (polygon.Length + 1 > output.Length) return -1;
        var edge = b - a;
        var tolerance = edge.Length() * .000001f;
        var previous = polygon[^1]; var previousD = Cross(edge, previous - a) * sign;
        if (Math.Abs(previousD) <= tolerance) previousD = 0;
        foreach (var current in polygon)
        {
            var currentD = Cross(edge, current - a) * sign;
            if (Math.Abs(currentD) <= tolerance) currentD = 0;
            if ((currentD >= 0) != (previousD >= 0)) output[n++] = Vector2.Lerp(previous, current, previousD / (previousD - currentD));
            if (currentD >= 0) output[n++] = current;
            previous = current; previousD = currentD;
        }
        // A crossing exactly at a seam endpoint can emit that endpoint twice.
        // Removing repeats prevents zero-area slivers from demanding new rays.
        var unique = 0;
        for (var i = 0; i < n; i++)
            if (unique == 0 || Vector2.DistanceSquared(output[i], output[unique - 1]) > 1e-14f) output[unique++] = output[i];
        if (unique > 1 && Vector2.DistanceSquared(output[0], output[unique - 1]) <= 1e-14f) unique--;
        return unique;
    }

    private static bool TryFace(TrailSurfaceHit hit, double at, out Face face)
    {
        face = default;
        if (!PositionValid(hit.Point) || !PositionValid(hit.A) || !PositionValid(hit.B) || !PositionValid(hit.C)) return false;
        var normal = TrailPlacement.SurfaceNormal(default, hit.A, hit.B, hit.C);
        if (normal.Y <= .35f) return false;
        face = new(hit.A, hit.B, hit.C, normal, at, Vector2.Min(Xz(hit.A), Vector2.Min(Xz(hit.B), Xz(hit.C))),
            Vector2.Max(Xz(hit.A), Vector2.Max(Xz(hit.B), Xz(hit.C))));
        if (Math.Abs(face.Height(Xz(hit.Point)) - hit.Point.Y) > .02f) return false;
        var sign = MathF.Sign(Cross(Xz(hit.B) - Xz(hit.A), Xz(hit.C) - Xz(hit.A)));
        return Cross(Xz(hit.B) - Xz(hit.A), Xz(hit.Point) - Xz(hit.A)) * sign >= -.0001f
            && Cross(Xz(hit.C) - Xz(hit.B), Xz(hit.Point) - Xz(hit.B)) * sign >= -.0001f
            && Cross(Xz(hit.A) - Xz(hit.C), Xz(hit.Point) - Xz(hit.C)) * sign >= -.0001f;
    }

    private static bool SameTriangle(Face a, Face b) => (a.A == b.A || a.A == b.B || a.A == b.C)
        && (a.B == b.A || a.B == b.B || a.B == b.C) && (a.C == b.A || a.C == b.B || a.C == b.C);
    private static bool PositionValid(Vector3 p) => TrailPlacement.IsFinite(p) && Vector3.Abs(p).LengthSquared() < 1e12f;
    private static bool Finite(Vector4 color) => float.IsFinite(color.X) && float.IsFinite(color.Y) && float.IsFinite(color.Z) && float.IsFinite(color.W);
    private static Vector2 Xz(Vector3 p) => new(p.X, p.Z);
    private static float Cross(Vector2 a, Vector2 b) => (float)((double)a.X * b.Y - (double)a.Y * b.X);
    private static float Area(ReadOnlySpan<Vector2> polygon)
    {
        var sum = 0f;
        for (var i = 1; i + 1 < polygon.Length; i++) sum += Cross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0]);
        return sum * .5f;
    }
    private static Vector2 Centroid(ReadOnlySpan<Vector2> polygon)
    {
        var sum = Vector2.Zero; foreach (var p in polygon) sum += p; return sum / polygon.Length;
    }
}
