using System.Numerics;
namespace XivWayfinder.Core;

/// <summary>Companion and downward destination poses for one local cursor.</summary>
public static class PointerPresentation
{
    public const float Period = 3.6f;
    public const float Bounce = .18f;
    public static MinionPose? Companion(Vector3 player, float rotation, Vector2 direction,
        MinionLayout layout, double now, bool reducedMotion)
    {
        if (!TrailPlacement.IsFinite(player) || !float.IsFinite(rotation)) return null;
        var at = MinionGlove.PlaceBeside(player, rotation, layout, 0);
        if (at is null) return null;
        var yaw = direction.LengthSquared() > 1e-6f && float.IsFinite(direction.X) && float.IsFinite(direction.Y)
            ? MinionGlove.Yaw(direction, layout.TurnDegrees) : MinionGlove.Yaw(Heading.Forward(rotation), layout.TurnDegrees);
        return new(at.Value + Vector3.UnitY * Bob(now, reducedMotion), yaw, 0);
    }
    public static MinionPose? Destination(Vector3? destination, bool inView, double now, bool reducedMotion)
    {
        if (!inView || destination is not { } at || !TrailPlacement.IsFinite(at)) return null;
        return new(at + Vector3.UnitY * (2.4f + Bob(now, reducedMotion)), 0, -MathF.PI / 2);
    }
    private static float Bob(double now, bool reducedMotion) => reducedMotion || !double.IsFinite(now)
        ? 0 : Pulse.Bob(now, Period, Bounce);
}
