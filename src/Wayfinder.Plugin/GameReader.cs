using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Wayfinder.Core;

namespace Wayfinder.Plugin;

/// <summary>A map's scale and offsets from the <c>Map</c> sheet.</summary>
internal readonly record struct MapInfo(uint MapId, ushort SizeFactor, short OffsetX, short OffsetY);

/// <summary>
/// Everything Wayfinder reads from the game: the map flag (<c>AgentMap.FlagMapMarkers</c>), the journal
/// (<c>QuestManager</c>) and the map's quest markers (<c>AgentMap.EventMarkers</c>), attuned aetherytes, and the
/// sheets behind zone names, maps and aetheryte positions. Only reads; nothing here changes game state. The
/// game-memory reads run on the framework thread; the sheet caches are locked because IPC may ask from elsewhere.
/// </summary>
internal sealed unsafe class GameReader(IDataManager data, IAetheryteList attuned, IPluginLog log)
{
    private readonly Lock gate = new();
    private readonly Dictionary<uint, List<AetheryteSpot>> aetherytes = [];
    private readonly Dictionary<uint, string> zoneNames = [];
    private List<Zone>? zones;
    private bool warnedQuest;

    /// <summary>The first map flag, when one is set.</summary>
    public Target? Flag()
    {
        var agent = AgentMap.Instance();
        if (agent == null || agent->FlagMarkerCount == 0)
            return null;
        var flags = agent->FlagMapMarkers;
        if (flags.Length == 0)
            return null;
        ref readonly var flag = ref flags[0];
        if (flag.TerritoryId == 0 || !float.IsFinite(flag.XFloat) || !float.IsFinite(flag.YFloat))
            return null;
        // XFloat/YFloat are world X and Z: what SetFlagMapMarker takes and the map converts for display
        return new Target(TargetSource.Flag, flag.TerritoryId, flag.XFloat, flag.YFloat, null, "map flag");
    }

    /// <summary>The next step of the tracked (or else the most likely) quest that has a known location.</summary>
    public Target? Quest(uint playerTerritory, Vector3 player)
    {
        try
        {
            var qm = QuestManager.Instance();
            if (qm == null)
                return null;
            var normal = qm->NormalQuests;
            var tracked = qm->TrackedQuests;
            var leads = new List<QuestLead>(normal.Length);
            for (var i = 0; i < normal.Length; i++)
            {
                ref readonly var q = ref normal[i];
                if (q.QuestId == 0)
                    continue;
                var order = -1;
                for (var j = 0; j < tracked.Length; j++)
                {
                    // QuestType 1 is taken to be a journal quest, Index its slot in NormalQuests (not yet checked in game)
                    if (tracked[j].QuestType == 1 && tracked[j].Index == i)
                    {
                        order = j;
                        break;
                    }
                }

                leads.Add(new QuestLead(q.QuestId, q.Sequence, q.IsHidden, order, q.IsPriority, i));
            }

            foreach (var lead in QuestPick.Order(leads))
            {
                var name = QuestName(lead.QuestId);
                var place = FromMapMarkers(lead.QuestId, name, playerTerritory, player) ?? FromQuestSheet(lead, name, playerTerritory, player);
                if (place is { } p)
                    return new Target(TargetSource.Quest, p.TerritoryId, p.X, p.Z, p.Y, p.Label);
            }
        }
        catch (Exception ex)
        {
            if (!warnedQuest)
                log.Warning(ex, "Wayfinder: reading the journal failed; quest targets are off until the plugin reloads");
            warnedQuest = true;
        }

        return null;
    }

    private static QuestLocation? FromMapMarkers(ushort questId, string name, uint playerTerritory, Vector3 player)
    {
        var agent = AgentMap.Instance();
        if (agent == null)
            return null;
        var found = new List<QuestLocation>();
        foreach (ref readonly var m in agent->EventMarkers.AsSpan())
        {
            if (QuestPick.MatchesObjective(m.ObjectiveId, questId) && m.TerritoryTypeId != 0)
                found.Add(new QuestLocation(m.TerritoryTypeId, m.Position.X, m.Position.Y, m.Position.Z, name));
        }

        return QuestPick.Nearest(found, playerTerritory, player);
    }

    private QuestLocation? FromQuestSheet(QuestLead lead, string name, uint playerTerritory, Vector3 player)
    {
        if (!data.GetExcelSheet<Quest>().TryGetRow(QuestPick.QuestRowBase + lead.QuestId, out var quest))
            return null;
        var sequences = new List<byte>();
        foreach (var todo in quest.TodoParams)
            sequences.Add(todo.ToDoCompleteSeq);
        var found = new List<QuestLocation>();
        foreach (var index in QuestPick.TodoRowsFor(lead.Sequence, sequences))
        {
            foreach (var level in quest.TodoParams[index].ToDoLocation)
            {
                if (level.RowId != 0 && level.ValueNullable is { } l && l.Territory.RowId != 0)
                    found.Add(new QuestLocation(l.Territory.RowId, l.X, l.Y, l.Z, name));
            }
        }

        return QuestPick.Nearest(found, playerTerritory, player);
    }

    private string QuestName(ushort questId) =>
        data.GetExcelSheet<Quest>().TryGetRow(QuestPick.QuestRowBase + questId, out var q) ? q.Name.ExtractText() : "quest";

    /// <summary>Aetherytes in a territory with their world positions, and whether the player has attuned to each.</summary>
    public IReadOnlyList<AetheryteSpot> AetherytesIn(uint territory)
    {
        List<AetheryteSpot> spots;
        lock (gate)
        {
            if (!aetherytes.TryGetValue(territory, out spots!))
            {
                spots = LoadAetherytes(territory);
                aetherytes[territory] = spots;
            }
        }

        if (spots.Count == 0)
            return spots;
        var have = new HashSet<uint>();
        for (var i = 0; i < attuned.Length; i++)
        {
            if (attuned[i] is { } e)
                have.Add(e.AetheryteId);
        }

        return spots.Select(s => s with { Unlocked = have.Contains(s.Id) }).ToList();
    }

    private List<AetheryteSpot> LoadAetherytes(uint territory)
    {
        var result = new List<AetheryteSpot>();
        var markers = data.GetSubrowExcelSheet<MapMarker>();
        foreach (var a in data.GetExcelSheet<Aetheryte>())
        {
            if (!a.IsAetheryte || a.Invisible || a.Territory.RowId != territory)
                continue;
            var name = a.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            if (name.Length == 0)
                continue;
            Vector2? at = null;
            if (a.Map.ValueNullable is { } map && markers.TryGetRow(map.MapMarkerRange, out var rows))
            {
                foreach (var m in rows)
                {
                    // DataType 3: the marker is an aetheryte, DataKey its row
                    if (m.DataType == 3 && m.DataKey.RowId == a.RowId)
                    {
                        at = new Vector2(MapCoords.PixelToWorld(m.X, map.SizeFactor, map.OffsetX), MapCoords.PixelToWorld(m.Y, map.SizeFactor, map.OffsetY));
                        break;
                    }
                }
            }

            if (at is null && a.Level.Count > 0 && a.Level[0].ValueNullable is { } level)
                at = new Vector2(level.X, level.Z);
            if (at is { } p)
                result.Add(new AetheryteSpot(a.RowId, name, territory, p.X, p.Y, false));
        }

        return result;
    }

    /// <summary>The map a territory normally shows (its <c>TerritoryType.Map</c>), or a specific map row.</summary>
    public MapInfo? Map(uint territory, uint mapId = 0)
    {
        if (mapId == 0 && data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t))
            mapId = t.Map.RowId;
        if (mapId == 0 || !data.GetExcelSheet<Map>().TryGetRow(mapId, out var map))
            return null;
        return new MapInfo(mapId, map.SizeFactor, map.OffsetX, map.OffsetY);
    }

    public string ZoneName(uint territory)
    {
        if (territory == 0)
            return "";
        lock (gate)
        {
            if (zoneNames.TryGetValue(territory, out var cached))
                return cached;
            var name = data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out var t)
                ? t.PlaceName.ValueNullable?.Name.ExtractText() ?? ""
                : "";
            if (name.Length == 0)
                name = "zone " + territory;
            zoneNames[territory] = name;
            return name;
        }
    }

    /// <summary>Every zone with a name and a map, for <c>/wayfinder X Y zone</c>.</summary>
    public IReadOnlyList<Zone> Zones()
    {
        lock (gate)
        {
            if (zones is not null)
                return zones;
            zones = [];
            foreach (var t in data.GetExcelSheet<TerritoryType>())
            {
                if (t.Map.RowId == 0)
                    continue;
                var name = t.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
                if (name.Length > 0)
                    zones.Add(new Zone(t.RowId, name));
            }

            return zones;
        }
    }
}
