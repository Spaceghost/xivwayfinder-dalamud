namespace Wayfinder.Core;

/// <summary>What the game is doing right now, as far as showing a pointer is concerned.</summary>
public readonly record struct Situation(
    bool HasPlayer,
    bool UiHidden,
    bool Cutscene,
    bool BetweenAreas,
    bool GPose,
    bool InCombat,
    bool InDuty);

/// <summary>The player's choices about when to step aside.</summary>
public readonly record struct VisibilityRules(bool Enabled, bool HideInCombat, bool HideInDuty);

public static class Visibility
{
    /// <summary>
    /// Whether to draw, and if not, why (for the settings window and <c>/wayfinder status</c>). Cutscenes, zone
    /// changes, group pose and a hidden game UI always hide it; combat and duties hide it when the player asks.
    /// </summary>
    public static (bool Show, string Reason) Decide(Situation s, VisibilityRules rules)
    {
        if (!rules.Enabled)
            return (false, "switched off");
        if (!s.HasPlayer)
            return (false, "no character");
        if (s.BetweenAreas)
            return (false, "changing zone");
        if (s.Cutscene)
            return (false, "cutscene");
        if (s.GPose)
            return (false, "group pose");
        if (s.UiHidden)
            return (false, "game UI hidden");
        if (s.InCombat && rules.HideInCombat)
            return (false, "in combat");
        if (s.InDuty && rules.HideInDuty)
            return (false, "in a duty");
        return (true, "showing");
    }
}
