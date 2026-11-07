using System.Text.Json;

namespace XivWayfinder.Core;

/// <summary>Assembly-independent, bounded itinerary exchange for sibling mods.</summary>
public static class JourneyExchange
{
    public static bool Append(JourneyPlan plan, string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 32768) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) return false;
            var stops = root.GetProperty("stops");
            if (stops.ValueKind != JsonValueKind.Array || stops.GetArrayLength() == 0
                || stops.GetArrayLength() > JourneyPlan.MaxStops - plan.Stops.Count) return false;
            var pending = new JourneyPlan();
            foreach (var stop in stops.EnumerateArray())
            {
                var target = WayfinderIpc.Validate(stop.GetProperty("territoryId").GetUInt32(),
                    stop.GetProperty("x").GetSingle(), stop.GetProperty("z").GetSingle(),
                    stop.TryGetProperty("label", out var label) ? label.GetString() : null, 0, out _);
                if (target is null) return false;
                if (stop.TryGetProperty("y", out var y) && y.ValueKind != JsonValueKind.Null)
                    target = target with { Y = y.GetSingle() };
                if (!pending.Add(target)) return false;
            }
            // Validate the entire batch before mutating the player's itinerary.
            plan.Stops.AddRange(pending.Stops);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException)
        { return false; }
    }

    public static string Snapshot(JourneyPlan plan) => JsonSerializer.Serialize(new
    {
        version = 1,
        index = plan.Index,
        running = plan.Running,
        stops = plan.Stops.Select(t => new { territoryId = t.TerritoryId, x = t.X, y = t.Y, z = t.Z, label = t.Label }),
    });

    public static bool Control(JourneyPlan plan, string action)
    {
        switch (action)
        {
            case "resume": return plan.Resume();
            case "pause": plan.Running = false; return true;
            case "next": return plan.Advance();
            case "previous": return plan.Back();
            default: return false;
        }
    }
}
