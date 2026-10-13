using System.Numerics;
using System.Text.Json;

namespace XivWayfinder.Core;

/// <summary>Bounded, versioned JSON boundary; consumers need no Wayfinder assembly.</summary>
public static class RouteSnapshot
{
    public const int MaxPoints = 512;

    public static string Encode(WayfinderState state, ReadOnlySpan<Vector3> path, long unixMilliseconds,
        RouteVisual? visual = null)
    {
        var valid = state.Enabled && state.Visible && state.Navmesh && state.Guide.Kind == GuideKind.Walk
            && state.Guide.Target?.TerritoryId == state.PlayerTerritoryId && path.Length is >= 2 and <= MaxPoints;
        foreach (var p in path)
            valid &= MapCoords.IsPlausibleWorldCoord(p.X) && MapCoords.IsPlausibleWorldCoord(p.Y) && MapCoords.IsPlausibleWorldCoord(p.Z);
        var cue = JourneyCue.From(state.Guide, state.ZoneName, valid, state.RouteNote);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteNumber("territoryId", state.PlayerTerritoryId);
            w.WriteNumber("generatedAt", unixMilliseconds);
            w.WriteBoolean("walkable", valid);
            w.WriteString("action", cue.Action);
            w.WriteString("next", cue.Next);
            w.WriteStartArray("points");
            if (valid)
                foreach (var p in path)
                {
                    w.WriteStartArray(); w.WriteNumberValue(p.X); w.WriteNumberValue(p.Y); w.WriteNumberValue(p.Z); w.WriteEndArray();
                }
            w.WriteEndArray();
            if (valid && visual is not null)
            {
                w.WriteStartObject("visual");
                w.WriteNumber("progress", visual.Progress);
                w.WriteNumber("guideArc", visual.GuideArc);
                w.WriteNumber("lookAheadArc", visual.LookAheadArc);
                WritePoint(w, "guide", visual.GuidePosition);
                WritePoint(w, "lookAhead", visual.LookAheadPosition);
                w.WriteStartArray("color");
                w.WriteNumberValue(visual.Color.X); w.WriteNumberValue(visual.Color.Y);
                w.WriteNumberValue(visual.Color.Z); w.WriteNumberValue(visual.Color.W);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WritePoint(Utf8JsonWriter writer, string name, Vector3 point)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(point.X); writer.WriteNumberValue(point.Y); writer.WriteNumberValue(point.Z);
        writer.WriteEndArray();
    }
}
