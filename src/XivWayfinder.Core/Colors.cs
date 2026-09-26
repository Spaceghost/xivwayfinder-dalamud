using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>Colours for ImGui's draw list, which takes 0xAABBGGRR.</summary>
public static class Colors
{
    /// <summary>Soft gold, the bead's default: the family's dark-ink, gold and glass look.</summary>
    public static readonly Vector4 Gold = new(1f, 0.82f, 0.42f, 1f);

    /// <summary>The glove's white, a little warm.</summary>
    public static readonly Vector4 Ivory = new(1f, 0.98f, 0.94f, 1f);

    /// <summary>Outlines and text shadows.</summary>
    public static readonly Vector4 Ink = new(0.07f, 0.08f, 0.12f, 1f);

    /// <summary>The Main Scenario's amber, like the flame of the game's own Main Scenario quest icon.</summary>
    public static readonly Vector4 MainScenario = new(1f, 0.56f, 0.16f, 1f);

    /// <summary>The bead's colour for a target: the Main Scenario's when it is on (<paramref name="mainScenarioStyle"/>), else the chosen one.</summary>
    public static Vector4 For(Target? target, Vector4 bead, bool mainScenarioStyle) =>
        mainScenarioStyle && target is { MainScenario: true } ? MainScenario : bead;

    /// <summary>Packs an RGBA colour with an extra opacity factor; components are clamped to 0..1.</summary>
    public static uint Pack(Vector4 rgba, float alpha = 1f)
    {
        static uint Byte(float v) => (uint)MathF.Round(Math.Clamp(float.IsFinite(v) ? v : 0f, 0f, 1f) * 255f);
        return (Byte(rgba.W * alpha) << 24) | (Byte(rgba.Z) << 16) | (Byte(rgba.Y) << 8) | Byte(rgba.X);
    }

    /// <summary>Mixes towards white by <paramref name="t"/> (0..1), for the bead's bright core.</summary>
    public static Vector4 Lighten(Vector4 rgba, float t)
    {
        var k = Math.Clamp(t, 0f, 1f);
        return new Vector4(rgba.X + (1f - rgba.X) * k, rgba.Y + (1f - rgba.Y) * k, rgba.Z + (1f - rgba.Z) * k, rgba.W);
    }
}
