using System.Numerics;

namespace XivWayfinder.Core;

public readonly record struct FairyMote(Vector3 GroundPosition, float Lift, float Radius, float Opacity);

/// <summary>Small deterministic lights flowing along the confirmed route, never a shortcut between corners.</summary>
public static class FairyTrail
{
    public const int MaxMotes = 96;
    private const float Spacing = .55f;

    public static int Sample(ReadOnlySpan<Vector3> path, float progress, float length, double time,
        float period, bool reducedMotion, Span<FairyMote> into, float startAhead = .12f)
    {
        if (path.Length < 2 || !float.IsFinite(progress) || progress < 0 || !float.IsFinite(length)
            || length <= 0 || !double.IsFinite(time) || time < 0 || !float.IsFinite(period) || period <= 0
            || !float.IsFinite(startAhead) || startAhead < 0 || startAhead >= length) return 0;
        foreach (var point in path) if (!TrailPlacement.IsFinite(point)) return 0;
        var total = PathTrail.Length(path);
        if (!float.IsFinite(total) || progress >= total) return 0;
        length = Math.Min(length, 50);
        var end = Math.Min(total, progress + length);
        var first = Math.Max(0, (int)MathF.Floor(Math.Min(progress + startAhead, 10_000_000) / Spacing) - 2);
        if (progress > 10_000_000) return 0;
        var count = 0;
        var limit = Math.Min(into.Length, MaxMotes);
        // At most 96 cells irrespective of the route length or elapsed time.
        for (var cell = first; count < limit && cell < first + MaxMotes; cell++)
        {
            var seed = Fraction(cell * .61803398875);
            var age = reducedMotion ? seed : Fraction(time / Math.Clamp(period, .6f, 8f) + seed);
            // Keep each light's floor query fixed on the route. Animating the
            // arc makes every exact collision-cache key new each frame, so a
            // bounded sampler can never warm the trail beyond a few lights.
            // Lifetime, lift and brightness still carry the flow outward.
            var arc = (cell + (float)seed) * Spacing;
            if (arc > end) break;
            var ahead = arc - progress;
            if (ahead <= startAhead) continue;
            var near = Pulse.SmoothStep(startAhead, startAhead + .9f, ahead);
            var far = 1 - Pulse.SmoothStep(length * .7f, length, ahead);
            var life = reducedMotion ? .65f : MathF.Pow(MathF.Sin((float)age * MathF.PI), 2);
            var breath = reducedMotion ? .8f : .62f + .38f * Pulse.TrailAt(ahead / length, time, period);
            var alpha = near * far * life * breath;
            if (alpha < .005f) continue;
            // Tiny vertical wisps decorate a verified floor point; the caller
            // resolves terrain and native scene depth before drawing them.
            var lift = reducedMotion ? .04f : .025f + .13f * (1 - (float)age);
            var radius = .045f + .035f * (float)Fraction(cell * .754877666);
            into[count++] = new(PathTrail.At(path, arc), lift, radius, alpha);
        }
        return count;
    }

    private static double Fraction(double value) => value - Math.Floor(value);
}
