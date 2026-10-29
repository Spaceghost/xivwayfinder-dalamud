using System.Numerics;
namespace XivWayfinder.Core;

public readonly record struct PointerJourneyFrame(MinionPose? Pose, bool AtDestination, float Hum, bool Chime);

/// <summary>One cursor changes roles; arrival sounds once for each approach to a destination.</summary>
public sealed class PointerJourney
{
    private Target? target;
    private Vector3? position;
    private bool approached, chimed;
    public void Hide() { position = null; approached = false; }
    public PointerJourneyFrame Update(MinionPose? companion, Target? destination, bool seesDestination,
        bool arrived, Vector3 player, double now, float dt, bool reducedMotion)
    {
        if (companion is null) { Hide(); return default; }
        if (!SameDestination(target, destination)) { target = destination; approached = chimed = false; }
        var marker = PointerPresentation.Destination(destination?.Y is not null ? destination.Position(0) : null,
            seesDestination, now, reducedMotion);
        var desired = marker ?? companion;
        if (marker is not null && !arrived) approached = true;
        var ring = arrived && approached && !chimed;
        if (ring) chimed = true;
        var pose = desired.Value;
        dt = float.IsFinite(dt) ? Math.Clamp(dt, 0, .1f) : 0;
        // Rush over the last clear sight line, then remain centred on the spot.
        // Reduced motion switches roles directly without travel or bouncing.
        if (position is { } previous && !reducedMotion)
        {
            var delta = pose.Position - previous; var distanceToPose = delta.Length();
            var step = Math.Min(distanceToPose * (1 - MathF.Pow(.5f, dt / .10f)), 28 * dt);
            position = distanceToPose > 1e-6f ? previous + delta * (step / distanceToPose) : pose.Position;
        }
        else position = pose.Position;
        var distance = marker is not null && destination is not null ? Vector3.Distance(player, destination.Position(player.Y)) : float.PositiveInfinity;
        var hum = marker is not null && !arrived && !chimed && float.IsFinite(distance)
            ? .15f + .85f / (1 + distance / 8) : 0;
        return new(pose with { Position = position.Value }, marker is not null, hum, ring);
    }
    private static bool SameDestination(Target? a, Target? b) => a is null ? b is null : b is not null
        && a.TerritoryId == b.TerritoryId && a.X == b.X && a.Z == b.Z && a.Y == b.Y;
}

/// <summary>Quiet locally synthesised hum and a decaying bell; no audio files or network.</summary>
public static class PointerSound
{
    public static short Sample(double time, float hum, double bellAge, float volume)
    {
        if (!double.IsFinite(time) || !float.IsFinite(hum) || !float.IsFinite(volume)) return 0;
        hum = Math.Clamp(hum, 0, 1); volume = Math.Clamp(volume, 0, 1);
        var value = hum * .075 * (Math.Sin(2 * Math.PI * 130.8128 * time)
            + .35 * Math.Sin(2 * Math.PI * 261.6256 * time) + .15 * Math.Sin(2 * Math.PI * 392.4384 * time));
        if (double.IsFinite(bellAge) && bellAge >= 0 && bellAge < 6)
        {
            var strike = Math.Min(1, bellAge / .012);
            value += .24 * strike * (Math.Exp(-bellAge / 1.7) * Math.Sin(2 * Math.PI * 587.33 * bellAge)
                + .45 * Math.Exp(-bellAge / .9) * Math.Sin(2 * Math.PI * 1174.66 * bellAge)
                + .15 * Math.Exp(-bellAge / .5) * Math.Sin(2 * Math.PI * 1621.03 * bellAge));
        }
        return (short)Math.Round(Math.Tanh(value * volume) * short.MaxValue);
    }
}
