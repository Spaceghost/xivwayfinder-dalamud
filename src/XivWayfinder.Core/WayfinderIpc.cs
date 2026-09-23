using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XivWayfinder.Core;

/// <summary>A snapshot of what XivWayfinder is doing, for <c>XivWayfinder.v1.GetState</c> and <c>/wayfinder status</c>.</summary>
public sealed record WayfinderState
{
    public bool Enabled { get; init; }
    public bool Visible { get; init; }
    public string Reason { get; init; } = "";
    public SourceMode Mode { get; init; }
    public PointerStyle Style { get; init; }
    public uint PlayerTerritoryId { get; init; }
    public Guide Guide { get; init; } = Guide.Nothing;
    public string ZoneName { get; init; } = "";
    public bool Navmesh { get; init; }

    /// <summary>When not following a path, why: vnavmesh's state ("not installed", "no path found", ...).</summary>
    public string RouteNote { get; init; } = "";
}

/// <summary>
/// The IPC functions' argument checks and the JSON <c>XivWayfinder.v1.GetState</c> returns. Other plugins' input is
/// checked here, never trusted: numbers must be finite and inside a zone's range, labels are trimmed and cleaned.
/// </summary>
public static class WayfinderIpc
{
    public const int MaxLabel = 120;

    /// <summary>
    /// Checks a <c>XivWayfinder.v1.SetTarget</c> call. <paramref name="territoryId"/> 0 means the player's current
    /// territory, <paramref name="currentTerritory"/>. Returns null, with a reason, when the call is refused.
    /// </summary>
    public static Target? Validate(uint territoryId, float x, float z, string? label, uint currentTerritory, out string error)
    {
        var territory = territoryId == 0 ? currentTerritory : territoryId;
        if (territory == 0)
        {
            error = "no territory: the player is not in one, and none was given";
            return null;
        }

        if (!MapCoords.IsPlausibleWorldCoord(x) || !MapCoords.IsPlausibleWorldCoord(z))
        {
            error = "x and z must be finite world coordinates within ±5000";
            return null;
        }

        error = "";
        return new Target(TargetSource.Explicit, territory, x, z, null, CleanLabel(label));
    }

    /// <summary>Printable text only, single spaces, at most <see cref="MaxLabel"/> characters.</summary>
    public static string CleanLabel(string? label)
    {
        if (string.IsNullOrEmpty(label))
            return "";
        var sb = new StringBuilder(Math.Min(label.Length, MaxLabel));
        var space = false;
        for (var i = 0; i < label.Length && sb.Length < MaxLabel; i++)
        {
            var c = label[i];
            if (char.IsHighSurrogate(c) && i + 1 < label.Length && char.IsLowSurrogate(label[i + 1]))
            {
                if (sb.Length + 2 > MaxLabel)
                    break;
                if (space && sb.Length > 0)
                    sb.Append(' ');
                space = false;
                sb.Append(c).Append(label[++i]);
                continue;
            }

            var category = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c) || char.IsControl(c) || category is UnicodeCategory.Surrogate or UnicodeCategory.Format or UnicodeCategory.PrivateUse)
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0)
                sb.Append(' ');
            space = false;
            sb.Append(c);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The state as JSON; see docs/IPC.md for the fields.</summary>
    public static string StateJson(WayfinderState state)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteBoolean("enabled", state.Enabled);
            w.WriteBoolean("visible", state.Visible);
            w.WriteString("reason", CleanLabel(state.Reason));
            w.WriteString("mode", Name(state.Mode));
            w.WriteString("style", state.Style.ToString().ToLowerInvariant());
            w.WriteNumber("playerTerritoryId", state.PlayerTerritoryId);
            w.WriteString("guide", state.Guide.Kind.ToString().ToLowerInvariant());
            w.WriteBoolean("navmesh", state.Navmesh);
            w.WriteString("routeNote", CleanLabel(state.RouteNote));
            if (state.Guide.Target is { } t)
            {
                w.WriteStartObject("target");
                w.WriteString("source", Name(t.Source));
                w.WriteNumber("territoryId", t.TerritoryId);
                w.WriteString("zone", CleanLabel(state.ZoneName));
                Number(w, "x", t.X);
                Number(w, "z", t.Z);
                if (t.Y is { } y)
                    Number(w, "y", y);
                else
                    w.WriteNull("y");
                w.WriteString("label", CleanLabel(t.Label));
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("target");
            }

            if (state.Guide.Kind is GuideKind.Walk or GuideKind.Arrived)
                Number(w, "distance", state.Guide.Distance);
            else
                w.WriteNull("distance");

            if (state.Guide.Aetheryte is { } a)
            {
                w.WriteStartObject("aetheryte");
                w.WriteNumber("id", a.Id);
                w.WriteString("name", CleanLabel(a.Name));
                w.WriteBoolean("attuned", a.Unlocked);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("aetheryte");
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>One line for chat.</summary>
    public static string StatusLine(WayfinderState state)
    {
        var g = state.Guide;
        var what = g.Target is { } t ? $"{Name(t.Source)} \"{(t.Label.Length > 0 ? t.Label : "unnamed")}\"" : "nothing";
        var where = g.Kind switch
        {
            GuideKind.Walk => string.Create(CultureInfo.InvariantCulture, $"{g.Distance:0} yalms away{Route(state)}"),
            GuideKind.Arrived => "you are there",
            GuideKind.Teleport => $"in {state.ZoneName}: teleport to {g.Aetheryte!.Name}{(g.Aetheryte.Unlocked ? "" : " (not attuned yet)")}",
            GuideKind.Elsewhere => $"in {state.ZoneName}, which has no aetheryte",
            _ => "no target",
        };
        return $"pointing at {what}: {where} · following {Name(state.Mode)} · {(state.Visible ? "showing" : "hidden: " + state.Reason)}";
    }

    private static string Route(WayfinderState state) =>
        state.Navmesh ? ", along vnavmesh's path"
        : state.RouteNote.Length > 0 ? $", in a straight line (vnavmesh: {state.RouteNote})"
        : "";

    public static string Name(SourceMode mode) => mode switch
    {
        SourceMode.Explicit => "target",
        _ => mode.ToString().ToLowerInvariant(),
    };

    public static string Name(TargetSource source) => source switch
    {
        TargetSource.Explicit => "target",
        _ => source.ToString().ToLowerInvariant(),
    };

    private static void Number(Utf8JsonWriter w, string name, float value)
    {
        if (float.IsFinite(value))
            w.WriteNumber(name, Math.Round(value, 2));
        else
            w.WriteNull(name);
    }
}
