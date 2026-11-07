using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>Distinguishes a positively observed obstacle being assessed from an unavailable collision service.</summary>
public sealed class GuideJumpEvidenceProgress
{
    public const double MaximumCollectionSeconds = 2;
    public const double MaximumStallSeconds = .5;
    public const double CriticalFreshnessSeconds = .25;
    public const int SceneQueryBudget = 32;
    public const int MaximumScenes = 4;
    public const int TrajectorySegments = 16;
    public const int MaximumBodySlices = 7; // leaves one four-scene landing ray in the frame budget
    public const int FootprintSamples = 5; // center and four cardinal edges
    private double began = double.NaN, progressed = double.NaN;
    public bool ObstacleConfirmed { get; private set; }
    public bool Stopped { get; private set; }
    public GuideJumpEvidence Incomplete => ObstacleConfirmed ? GuideJumpEvidence.Pending : GuideJumpEvidence.Unknown;
    public void Reset() { ObstacleConfirmed = false; Stopped = false; began = progressed = double.NaN; }
    public void Begin(double now) { Reset(); began = progressed = now; }
    public void MadeProgress(double now) { if (!Stopped && double.IsFinite(now) && now >= progressed) progressed = now; }
    public bool Expired(double now) => !double.IsFinite(now) || !double.IsFinite(began) || now < began
        || now - began > MaximumCollectionSeconds || now - progressed > MaximumStallSeconds;
    public static bool Fresh(double verified, double now) => double.IsFinite(verified) && double.IsFinite(now)
        && now >= verified && now - verified <= CriticalFreshnessSeconds;
    public void Stop() { ObstacleConfirmed = false; Stopped = true; }

    public bool ObserveForwardBlocker(GuideJumpRequest request, GuideJumpFloor takeoff, Vector3 point, Vector3 normal)
    {
        if (Stopped || !request.MatchesRoute() || takeoff.Evidence != GuideJumpEvidence.Clear
            || !TrailPlacement.IsFinite(takeoff.Position) || !float.IsFinite(takeoff.NormalY) || takeoff.NormalY < .55f
            || !TrailPlacement.IsFinite(point) || !TrailPlacement.IsFinite(normal)
            || normal.LengthSquared() < .5f || Math.Abs(normal.Y) / normal.Length() >= .55f) return false;
        var start = new Vector2(request.Start.X, request.Start.Z);
        var end = new Vector2(request.End.X, request.End.Z);
        var hit = new Vector2(point.X, point.Z);
        var delta = end - start;
        var fraction = Vector2.Dot(hit - start, delta) / delta.LengthSquared();
        var expected = takeoff.Position.Y + (request.End.Y - request.Start.Y) * fraction + .08f;
        if (!float.IsFinite(fraction) || fraction is <= .01f or >= .99f
            || Vector2.Distance(hit, start + delta * fraction) > .02f || Math.Abs(point.Y - expected) > .02f) return false;
        ObstacleConfirmed = true;
        return true;
    }
}
