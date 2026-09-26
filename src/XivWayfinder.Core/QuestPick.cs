using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>An accepted quest from the journal: <c>QuestManager.NormalQuests</c> plus whether it is tracked.</summary>
public readonly record struct QuestLead(ushort QuestId, byte Sequence, bool Hidden, int TrackedOrder, bool Priority, int JournalIndex)
{
    public bool Tracked => TrackedOrder >= 0;
}

/// <summary>A place a quest step happens: world position in a territory, named for the label.</summary>
public readonly record struct QuestLocation(uint TerritoryId, float X, float Y, float Z, string Label);

/// <summary>A journal quest with where its next step is.</summary>
public readonly record struct QuestFound(QuestLead Lead, QuestLocation Place);

/// <summary>How the quest to point at is chosen.</summary>
public enum QuestChoice
{
    /// <summary>The next step nearest you, of every quest you have accepted.</summary>
    Nearest,

    /// <summary>The tracked quest first, then a priority quest, then the journal order.</summary>
    Tracked,
}

/// <summary>
/// Which quest's next step to point at, and where it is. The quest comes first from the tracked list (the one the
/// player chose to follow), then a priority quest, then the journal order. Where comes first from what the game's
/// own map draws for that quest (<c>AgentMap.EventMarkers</c>, live), then from the quest's to-do list in the game
/// data (<c>Quest.TodoParams</c> → <c>Level</c>), which also knows steps in other zones.
/// </summary>
public static class QuestPick
{
    /// <summary><c>Quest</c> sheet rows are the journal's quest id plus this.</summary>
    public const uint QuestRowBase = 0x10000;

    /// <summary>
    /// The journal, best candidate first; hidden quests are left out. <paramref name="prefer"/> (a quest the player
    /// asked to be shown, from a map link) comes before everything else.
    /// </summary>
    public static List<QuestLead> Order(IEnumerable<QuestLead> leads, ushort prefer = 0) =>
        leads.Where(q => q.QuestId != 0 && !q.Hidden)
            .OrderBy(q => prefer != 0 && q.QuestId == prefer ? 0 : 1)
            .ThenBy(q => q.Tracked ? 0 : q.Priority ? 1 : 2)
            .ThenBy(q => q.Tracked ? q.TrackedOrder : q.JournalIndex)
            .ThenBy(q => q.JournalIndex)
            .ToList();

    /// <summary>
    /// The quest to point at from those whose next step was found (<paramref name="found"/>, in <see cref="Order"/>'s
    /// order). A quest the player asked to be shown (<paramref name="prefer"/>) always comes first. Otherwise,
    /// <see cref="QuestChoice.Nearest"/> takes the step nearest the player (along the ground) in their own zone,
    /// and only with none there the first of the rest; <see cref="QuestChoice.Tracked"/> takes the first.
    /// </summary>
    public static QuestFound? Choose(IReadOnlyList<QuestFound> found, QuestChoice choice, ushort prefer, uint playerTerritory, Vector3 player)
    {
        if (found.Count == 0)
            return null;
        if (prefer != 0)
        {
            foreach (var f in found)
            {
                if (f.Lead.QuestId == prefer)
                    return f;
            }
        }

        if (choice != QuestChoice.Nearest)
            return found[0];
        QuestFound? best = null;
        var bestDistance = float.PositiveInfinity;
        foreach (var f in found)
        {
            if (f.Place.TerritoryId != playerTerritory)
                continue;
            var dx = f.Place.X - player.X;
            var dz = f.Place.Z - player.Z;
            var d = dx * dx + dz * dz;
            if (d < bestDistance)
            {
                best = f;
                bestDistance = d;
            }
        }

        return best ?? found[0];
    }

    /// <summary>Whether a journal quest is one of your current Main Scenario quests (the scenario guide's).</summary>
    public static bool IsMainScenario(ushort questId, ReadOnlySpan<ushort> mainScenario)
    {
        if (questId == 0)
            return false;
        foreach (var q in mainScenario)
        {
            if (q != 0 && (q == questId || q == questId + QuestRowBase || q + QuestRowBase == questId))
                return true;
        }

        return false;
    }

    /// <summary>A map marker's objective id names a quest either by journal id or by <c>Quest</c> row.</summary>
    public static bool MatchesObjective(uint objectiveId, ushort questId) =>
        questId != 0 && (objectiveId == questId || objectiveId == questId + QuestRowBase);

    /// <summary>
    /// Which of a quest's to-do rows describe the current step, from each row's <c>ToDoCompleteSeq</c>: the rows
    /// for exactly this sequence, or else those for the nearest later one (the step that ends the current
    /// sequence). Empty when nothing fits.
    /// </summary>
    public static List<int> TodoRowsFor(byte sequence, IReadOnlyList<byte> completeSequences)
    {
        var exact = new List<int>();
        for (var i = 0; i < completeSequences.Count; i++)
        {
            if (completeSequences[i] == sequence)
                exact.Add(i);
        }

        if (exact.Count > 0)
            return exact;
        var next = int.MaxValue;
        for (var i = 0; i < completeSequences.Count; i++)
        {
            if (completeSequences[i] > sequence && completeSequences[i] < next)
                next = completeSequences[i];
        }

        var later = new List<int>();
        if (next == int.MaxValue)
            return later;
        for (var i = 0; i < completeSequences.Count; i++)
        {
            if (completeSequences[i] == next)
                later.Add(i);
        }

        return later;
    }

    /// <summary>The location to point at: the nearest one in the player's territory, else the first elsewhere.</summary>
    public static QuestLocation? Nearest(IEnumerable<QuestLocation> locations, uint playerTerritory, Vector3 player)
    {
        QuestLocation? here = null;
        QuestLocation? elsewhere = null;
        var best = float.PositiveInfinity;
        foreach (var l in locations)
        {
            if (l.TerritoryId == 0 || !float.IsFinite(l.X) || !float.IsFinite(l.Z))
                continue;
            if (l.TerritoryId != playerTerritory)
            {
                elsewhere ??= l;
                continue;
            }

            var d = Vector2.Distance(new Vector2(player.X, player.Z), new Vector2(l.X, l.Z));
            if (d < best)
            {
                best = d;
                here = l;
            }
        }

        return here ?? elsewhere;
    }
}
