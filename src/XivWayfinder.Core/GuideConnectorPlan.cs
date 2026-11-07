using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>A query-confirmed connector followed by the unchanged main-route suffix.</summary>
public sealed record GuideConnectorPlan(Vector3[] Route, Vector3[] TargetRoute, float JoinArc,
    float ConnectorLength, Vector3 Start, Vector3 Join)
{
    private Vector3[] targetSnapshot = [];
    public const int MaxPoints = 512;
    public const float EndpointTolerance = .002f; // numerical equality, not permission to bridge a gap
    public const float MaxConnectorLength = 2048;

    public static bool TryJoin(Vector3[] mainRoute, Vector3 from, out float arc, out Vector3 join)
    {
        arc = 0; join = default;
        if (!Valid(mainRoute) || !Point(from)) return false;
        arc = PathTrail.Project(mainRoute, from, out _);
        join = PathTrail.At(mainRoute, arc);
        return float.IsFinite(arc) && Point(join);
    }

    public static bool TryCreate(Vector3[] mainRoute, Vector3 requestedStart, float joinArc,
        IReadOnlyList<Vector3>? queried, out GuideConnectorPlan? plan)
    {
        plan = null;
        if (!Valid(mainRoute) || !Point(requestedStart) || !float.IsFinite(joinArc) || joinArc < 0 ||
            joinArc > PathTrail.Length(mainRoute) || queried is null || queried.Count is < 2 or > MaxPoints) return false;
        var join = PathTrail.At(mainRoute, joinArc);
        if (Vector3.Distance(queried[0], requestedStart) > EndpointTolerance ||
            Vector3.Distance(queried[^1], join) > EndpointTolerance) return false;
        var corners = new Vector3[queried.Count];
        for (var i = 0; i < corners.Length; i++) corners[i] = queried[i];
        if (!Valid(corners)) return false;
        // Normalize only floating-point endpoint roundoff. Never prepend a
        // missing start, extend a partial path, or interpolate a missing leg.
        corners[0] = requestedStart; corners[^1] = join;
        var connectorLength = PathTrail.Length(corners);
        if (connectorLength > MaxConnectorLength) return false;
        var combined = new List<Vector3>(corners);
        var arc = 0f;
        for (var i = 1; i < mainRoute.Length; i++)
        {
            arc += Heading.FlatDistance(mainRoute[i - 1], mainRoute[i]);
            if (arc > joinArc && Vector3.Distance(combined[^1], mainRoute[i]) > .00001f)
                combined.Add(mainRoute[i]);
        }
        if (combined.Count > MaxPoints * 2) return false;
        plan = new(combined.ToArray(), mainRoute, joinArc, connectorLength, requestedStart, join)
        { targetSnapshot = (Vector3[])mainRoute.Clone() };
        return true;
    }

    public bool MatchesTarget(Vector3[] path) => ReferenceEquals(path, TargetRoute) &&
        path.AsSpan().SequenceEqual(targetSnapshot);

    public bool OnMainSuffix(CompanionRouteMotionFrame frame) => frame.HasPosition &&
        ReferenceEquals(frame.Route, Route) && frame.Arc >= ConnectorLength &&
        Vector3.Distance(frame.Position, PathTrail.At(TargetRoute, JoinArc + frame.Arc - ConnectorLength)) <= EndpointTolerance;

    internal static bool Valid(IReadOnlyList<Vector3>? points)
    {
        if (points is null || points.Count is < 2 or > MaxPoints) return false;
        var total = 0f;
        for (var i = 0; i < points.Count; i++)
        {
            if (!Point(points[i])) return false;
            if (i == 0) continue;
            var flat = Heading.FlatDistance(points[i - 1], points[i]);
            if (flat < .0001f && Math.Abs(points[i - 1].Y - points[i].Y) > EndpointTolerance) return false;
            total += flat;
        }
        return float.IsFinite(total) && total > .001f;
    }

    internal static bool Point(Vector3 p) => MapCoords.IsPlausibleWorldCoord(p.X) &&
        MapCoords.IsPlausibleWorldCoord(p.Y) && MapCoords.IsPlausibleWorldCoord(p.Z);
}
