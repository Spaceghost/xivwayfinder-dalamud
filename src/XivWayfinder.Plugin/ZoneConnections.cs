using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;
using XivWayfinder.Core;
namespace XivWayfinder.Plugin;

/// <summary>Reads actual zone-line coordinates from the installed game's layout data.</summary>
internal sealed class ZoneConnections
{
    private readonly Task<ZoneConnection[]> loading;
    private (uint From, uint To) last;
    private Vector3 lastPlayer;
    private long nextRefresh;
    private ZoneConnection? cached;
    public string Status => loading.IsCompletedSuccessfully ? $"{loading.Result.Length} zone connections ready" : loading.IsFaulted ? "zone connections could not be read" : "reading zone connections";
    public ZoneConnections(IDataManager data, IPluginLog log)
    {
        loading = Task.Run(() =>
        {
            var result = new List<ZoneConnection>();
            var zones = data.GetExcelSheet<TerritoryType>().Where(t => t.Map.RowId != 0 && t.TerritoryIntendedUse.RowId is 0 or 1 or 13).ToDictionary(t => t.RowId);
            var files = 0; var exits = 0; var failures = 0;
            foreach (var zone in zones.Values)
            {
                var bg = zone.Bg.ExtractText(); var slash = bg.LastIndexOf('/');
                if (slash < 0) continue;
                try
                {
                    var file = data.GetFile<LgbFile>("bg/" + bg[..(slash + 1)] + "planmap.lgb");
                    if (file is null) continue;
                    files++;
                    foreach (var layer in file.Layers)
                    {
                        // Zone-line layers are static; festival and quest-only layout layers are excluded.
                        if (layer.FestivalID != 0 || !layer.Name.StartsWith("LVD_zone", StringComparison.Ordinal)) continue;
                        foreach (var obj in layer.InstanceObjects)
                        {
                            if (obj.Object is not LayerCommon.ExitRangeInstanceObject exit) continue;
                            exits++;
                            if (!zones.TryGetValue(exit.TerritoryType, out var dest)) continue;
                            var p = obj.Transform.Translation;
                            result.Add(new(zone.RowId, exit.TerritoryType, new Vector3(p.X, p.Y, p.Z), $"Enter {dest.PlaceName.Value.Name.ExtractText()}"));
                        }
                    }
                }
                catch (Exception ex) { if (failures++ < 2) log.Warning(ex, "Wayfinder: cannot read exits for territory {Territory}", zone.RowId); }
            }
            log.Information("Wayfinder: loaded {Count} zone connections from {Zones} zones, {Files} files, {Exits} exits, {Failures} failures", result.Count, zones.Count, files, exits, failures);
            return result.ToArray();
        });
    }
    public ZoneConnection? Next(uint from, uint to, Vector3 player)
    {
        if (!loading.IsCompletedSuccessfully) return null;
        var now = Environment.TickCount64;
        if (last != (from, to) || now >= nextRefresh && Vector3.DistanceSquared(player, lastPlayer) > 1)
        {
            cached = ZoneRoute.Next(from, to, player, loading.Result);
            last = (from, to); lastPlayer = player; nextRefresh = now + 500;
        }
        return cached;
    }
}
