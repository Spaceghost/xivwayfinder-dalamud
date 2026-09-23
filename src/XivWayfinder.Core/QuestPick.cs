using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>An accepted quest from the journal: <c>QuestManager.NormalQuests</c> plus whether it is tracked.</summary>
public readonly record struct QuestLead(ushort QuestId, byte Sequence, bool Hidden, int TrackedOrder, bool Priority, int JournalIndex)
{
    public bool Tracked => TrackedOrder >= 0;
}

/// <summary>A place a quest step happens: world position in a territory, named for the label.</summary>
public readonly record struct QuestLocation(uint TerritoryId, float X, float Y, float Z, string Label);

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

    /// <summary>The journal, best candidate first; hidden quests are left out.</summary>
    public static List<QuestLead> Order(IEnumerable<QuestLead> leads) =>
        leads.Where(q => q.QuestId != 0 && !q.Hidden)
            .OrderBy(q => q.Tracked ? 0 : q.Priority ? 1 : 2)
            .ThenBy(q => q.Tracked ? q.TrackedOrder : q.JournalIndex)
            .ThenBy(q => q.JournalIndex)
            .ToList();

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
