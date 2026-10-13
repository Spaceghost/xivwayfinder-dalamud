namespace XivWayfinder.Core;

/// <summary>Read-only observed Teleport spell, not an intention or a menu-open action.</summary>
public static class TeleportCastObservation
{
    // Verified against the installed Action sheet and ClientStructs: Action/5
    // is Teleport, Action/6 is Return. GeneralAction/7 only opens its menu.
    public static bool TryRemaining(bool casting, byte actionType, uint actionId,
        float currentSeconds, float totalSeconds, out float remaining)
    {
        remaining = 0;
        if (!casting || actionType != 1 || actionId != 5
            || !float.IsFinite(currentSeconds) || !float.IsFinite(totalSeconds)
            || currentSeconds < 0 || totalSeconds <= 0) return false;
        remaining = Math.Max(0, totalSeconds - currentSeconds);
        return true;
    }
}
