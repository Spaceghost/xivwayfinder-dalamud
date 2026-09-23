using System.Numerics;

namespace Wayfinder.Core;

/// <summary>Where a target came from, in priority order.</summary>
public enum TargetSource
{
    /// <summary>Set by <c>/wayfinder x y</c>, <c>/wayfinder test</c> or another plugin over IPC.</summary>
    Explicit,

    /// <summary>The player's map flag.</summary>
    Flag,

    /// <summary>The next objective of the tracked (or first active) quest, where the game gives a location.</summary>
    Quest,
}

/// <summary>Which sources Wayfinder may follow: all of them by priority, or only one.</summary>
public enum SourceMode
{
    Auto,
    Explicit,
    Flag,
    Quest,
}

/// <summary>
/// Somewhere to go: a world position (X, Z; Y when it is known) in a territory (a <c>TerritoryType</c> row id).
/// </summary>
public sealed record Target(TargetSource Source, uint TerritoryId, float X, float Z, float? Y, string Label)
{
    public Vector3 Position(float fallbackY) => new(X, Y ?? fallbackY, Z);
}

/// <summary>Which sources are switched on in the settings.</summary>
public readonly record struct SourceToggles(bool Explicit, bool Flag, bool Quest)
{
    public static SourceToggles All => new(true, true, true);

    public bool Allows(TargetSource source) => source switch
    {
        TargetSource.Explicit => Explicit,
        TargetSource.Flag => Flag,
        TargetSource.Quest => Quest,
        _ => false,
    };
}

/// <summary>An aetheryte someone could teleport to, with its world position in its territory.</summary>
public sealed record AetheryteSpot(uint Id, string Name, uint TerritoryId, float X, float Z, bool Unlocked);

/// <summary>What the overlay should show.</summary>
public enum GuideKind
{
    /// <summary>No target, or none the settings allow.</summary>
    None,

    /// <summary>The target is in this zone: point at it.</summary>
    Walk,

    /// <summary>Within the arrival radius: a soft ring on the spot, no pointer.</summary>
    Arrived,

    /// <summary>The target is in another zone and has an aetheryte there: "teleport to X".</summary>
    Teleport,

    /// <summary>The target is in another zone with no known aetheryte: name the zone.</summary>
    Elsewhere,
}

/// <summary>The plan for this moment: what kind of hint, towards what, how far (yalms, same zone only).</summary>
public sealed record Guide(GuideKind Kind, Target? Target, float Distance, Vector2 Direction, AetheryteSpot? Aetheryte)
{
    public static readonly Guide Nothing = new(GuideKind.None, null, 0f, Vector2.Zero, null);
}

public static class TargetPicker
{
    /// <summary>
    /// The target to follow. Auto takes the first available of explicit, flag, quest; a single-source mode takes
    /// only that source. A source switched off in the settings is never taken, even in its own mode.
    /// </summary>
    public static Target? Pick(SourceMode mode, SourceToggles toggles, Target? explicitTarget, Target? flag, Target? quest)
    {
        Target? Allowed(Target? t, TargetSource s) => t is not null && toggles.Allows(s) ? t : null;
        return mode switch
        {
            SourceMode.Explicit => Allowed(explicitTarget, TargetSource.Explicit),
            SourceMode.Flag => Allowed(flag, TargetSource.Flag),
            SourceMode.Quest => Allowed(quest, TargetSource.Quest),
            _ => Allowed(explicitTarget, TargetSource.Explicit) ?? Allowed(flag, TargetSource.Flag) ?? Allowed(quest, TargetSource.Quest),
        };
    }

    /// <summary>
    /// Turns a target into a hint for a player standing at <paramref name="player"/> in
    /// <paramref name="territory"/>. <paramref name="aim"/> is where to point when it differs from the target
    /// (the next corner of a walkable path); null means straight at the target.
    /// </summary>
    public static Guide Plan(uint territory, Vector3 player, Target? target, float arriveRadius, IReadOnlyList<AetheryteSpot> aetherytes, Vector3? aim = null)
    {
        if (target is null)
            return Guide.Nothing;
        if (target.TerritoryId != territory)
        {
            var spot = NearestAetheryte(target, aetherytes);
            return new Guide(spot is null ? GuideKind.Elsewhere : GuideKind.Teleport, target, 0f, Vector2.Zero, spot);
        }

        var goal = target.Position(player.Y);
        var distance = Heading.FlatDistance(player, goal);
        if (distance <= MathF.Max(0f, arriveRadius))
            return new Guide(GuideKind.Arrived, target, distance, Vector2.Zero, null);
        var toward = aim is { } a && Heading.FlatDistance(player, a) > 0.25f ? a : goal;
        return new Guide(GuideKind.Walk, target, distance, Heading.DirectionTo(player, toward), null);
    }

    /// <summary>
    /// The aetheryte in the target's territory closest to it, preferring ones the player has attuned to; null when
    /// the territory has none.
    /// </summary>
    public static AetheryteSpot? NearestAetheryte(Target target, IReadOnlyList<AetheryteSpot> aetherytes)
    {
        AetheryteSpot? best = null;
        var bestScore = float.PositiveInfinity;
        foreach (var a in aetherytes)
        {
            if (a.TerritoryId != target.TerritoryId)
                continue;
            var d = new Vector2(a.X - target.X, a.Z - target.Z).Length();
            if (!float.IsFinite(d))
                continue;
            // an attuned aetheryte always beats one the player cannot use yet
            var score = d + (a.Unlocked ? 0f : 100_000f);
            if (score < bestScore || (score == bestScore && best is not null && a.Id < best.Id))
            {
                best = a;
                bestScore = score;
            }
        }

        return best;
    }
}

/// <summary>
/// When an explicit target counts as reached: the player has stood inside the arrival radius for a moment, so
/// walking past the spot does not clear it.
/// </summary>
public sealed class ArrivalTimer
{
    private double insideSince = double.NaN;

    /// <summary>True once the player has been within the radius for <paramref name="hold"/> seconds.</summary>
    public bool Update(bool inside, double now, double hold)
    {
        if (!inside)
        {
            insideSince = double.NaN;
            return false;
        }

        if (double.IsNaN(insideSince))
            insideSince = now;
        return now - insideSince >= hold;
    }

    public void Reset() => insideSince = double.NaN;
}
