namespace XivWayfinder.Core;

/// <summary>
/// Animation timing, all from one clock in seconds so a frame drop never makes anything jump: the bead's breathing
/// glow and bob, how bright it is for which way the character faces, the glove's tap, the trail's travelling
/// shimmer, and a frame-rate-independent easing for fades.
/// </summary>
public static class Pulse
{
    /// <summary>0..1..0 over <paramref name="period"/> seconds, a sine starting at the midpoint.</summary>
    public static float Breath(double t, float period)
    {
        if (!(period > 0f) || !double.IsFinite(t))
            return 0.5f;
        var phase = t / period % 1.0;
        return (float)(0.5 + 0.5 * Math.Sin(phase * Math.Tau));
    }

    /// <summary>A gentle up-and-down in yalms, ±<paramref name="amplitude"/>.</summary>
    public static float Bob(double t, float period, float amplitude) => (Breath(t, period) * 2f - 1f) * amplitude;

    /// <summary>
    /// The bead's opacity: dim (<paramref name="min"/>) while the character faces the right way, bright
    /// (<paramref name="max"/>) once it has turned away, eased so small wobbles near "right way" do not flicker,
    /// and breathing ±15 % on top.
    /// </summary>
    public static float Glow(float turnAway, float breath, float min, float max)
    {
        var eased = SmoothStep(0.03f, 0.6f, Math.Clamp(turnAway, 0f, 1f));
        var level = min + (max - min) * eased;
        return Math.Clamp(level * (0.85f + 0.3f * Math.Clamp(breath, 0f, 1f)), 0f, 1f);
    }

    /// <summary>
    /// The glove's tap: 0 at rest, a quick push to 1 along the pointing direction over the first quarter of the
    /// period, then an ease back. Reads as "this way!" rather than a wobble.
    /// </summary>
    public static float Tap(double t, float period)
    {
        if (!(period > 0f) || !double.IsFinite(t))
            return 0f;
        var phase = (float)(t / period % 1.0);
        if (phase < 0f)
            phase += 1f;
        return phase < 0.25f ? SmoothStep(0f, 1f, phase / 0.25f) : 1f - SmoothStep(0f, 1f, (phase - 0.25f) / 0.75f);
    }

    /// <summary>
    /// Brightness 0..1 of trail dot <paramref name="index"/> (0 nearest the character) of <paramref name="count"/>:
    /// a soft crest travels outward along the trail, once every <paramref name="period"/> seconds, and dots fade
    /// with distance.
    /// </summary>
    public static float Trail(int index, int count, double t, float period)
    {
        if (count <= 0 || index < 0 || index >= count)
            return 0f;
        return TrailAt((index + 0.5f) / count, t, period);
    }

    /// <summary>
    /// The same for a point <paramref name="along"/> of the way (0 at the player, 1 at the trail's far end): the
    /// crest travels outward along the route, and points fade with distance.
    /// </summary>
    public static float TrailAt(float along, double t, float period)
    {
        if (!float.IsFinite(along))
            return 0f;
        along = Math.Clamp(along, 0f, 1f);
        var crest = !(period > 0f) || !double.IsFinite(t) ? 0f : (float)(t / period % 1.0);
        var gap = MathF.Abs(along - crest);
        gap = MathF.Min(gap, 1f - gap);
        var wave = 1f - SmoothStep(0f, 0.25f, gap);
        var fade = 1f - 0.6f * along;
        return Math.Clamp((0.35f + 0.65f * wave) * fade, 0f, 1f);
    }

    /// <summary>
    /// Moves <paramref name="current"/> towards <paramref name="target"/> so that half the gap closes every
    /// <paramref name="halfLife"/> seconds, whatever the frame rate.
    /// </summary>
    public static float Approach(float current, float target, float dt, float halfLife)
    {
        if (!float.IsFinite(current))
            return target;
        if (!(halfLife > 0f) || !(dt > 0f))
            return dt > 0f ? target : current;
        var keep = MathF.Pow(0.5f, dt / halfLife);
        return target + (current - target) * keep;
    }

    /// <summary>Hermite 0..1 between two edges.</summary>
    public static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0)
            return x < edge0 ? 0f : 1f;
        var k = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return k * k * (3f - 2f * k);
    }
}
