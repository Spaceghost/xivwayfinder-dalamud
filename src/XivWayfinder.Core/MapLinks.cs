namespace XivWayfinder.Core;

/// <summary>What to do when the game opens its map to show a place.</summary>
public enum MapLinkAction
{
    /// <summary>Let the map open as usual.</summary>
    Open,

    /// <summary>Follow the map flag instead (a &lt;flag&gt; link in chat, a flag set a moment ago).</summary>
    FollowFlag,

    /// <summary>Follow this quest's next step instead (Show on Map from the journal or the quest list).</summary>
    FollowQuest,
}

/// <summary>
/// Map links as wayfinding: when the game opens its map to show a place (<c>AgentMap.OpenMap</c>), XivWayfinder can
/// lead there instead. Only the opens that point at one place are taken: a flag the player just set (clicking a
/// &lt;flag&gt; link sets the flag and opens the map at once; opening the map with a flag set long ago is only
/// looking at the map) and a quest's Show on Map. Every other open (the map key, the Teleport list, FATEs, gathering
/// and hunt logs) opens as usual, and so does any open with the bypass key held.
/// </summary>
public static class MapLinks
{
    /// <summary><c>MapType</c> values from FFXIVClientStructs' <c>AgentMap</c>.</summary>
    public const uint FlagMarkerType = 1;

    public const uint QuestLogType = 3;

    /// <summary>How recently the flag must have changed for a flag-type open to count as following a link, seconds.</summary>
    public const double FreshFlagSeconds = 2.0;

    public static MapLinkAction Decide(uint mapType, uint questId, bool enabled, bool bypassHeld, double sinceFlagChanged) =>
        !enabled || bypassHeld ? MapLinkAction.Open
        : mapType == FlagMarkerType && sinceFlagChanged >= 0 && sinceFlagChanged <= FreshFlagSeconds ? MapLinkAction.FollowFlag
        : mapType == QuestLogType && JournalId(questId) != 0 ? MapLinkAction.FollowQuest
        : MapLinkAction.Open;

    /// <summary>The journal's quest id from an open's quest id, which may be a <c>Quest</c> sheet row.</summary>
    public static ushort JournalId(uint questId) =>
        questId >= QuestPick.QuestRowBase ? (ushort)(questId - QuestPick.QuestRowBase) : (ushort)questId;
}
