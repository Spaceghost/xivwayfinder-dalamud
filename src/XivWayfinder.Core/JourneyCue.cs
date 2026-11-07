namespace XivWayfinder.Core;

/// <summary>Shared next-action guidance for Wayfinder, floor-map and task-list consumers.</summary>
public sealed record JourneyCue(string Action, string Next, bool HasWalkablePath)
{
    public static JourneyCue From(Guide guide, string zone, bool hasPath, string routeNote)
    {
        var destination = WayfinderIpc.CleanLabel(guide.Target?.Label);
        if (destination.Length == 0) destination = "your destination";
        zone = WayfinderIpc.CleanLabel(zone);
        if (zone.Length == 0) zone = "the destination zone";
        if (guide.TransitTerritory != 0)
            return new(guide.Kind == GuideKind.Arrived ? guide.TransitAction : $"Follow the guide: {guide.TransitAction}",
                $"Continue through the zone connection; then I will guide the next leg to {WayfinderIpc.CleanLabel(guide.FinalTarget?.Label)}", hasPath);
        return guide.Kind switch
        {
            GuideKind.Teleport when guide.Aetheryte is { Unlocked: true } a =>
                new($"Teleport to {WayfinderIpc.CleanLabel(a.Name)}", $"Then follow the path to {destination} in {zone}", false),
            GuideKind.Teleport when guide.Aetheryte is { } a =>
                new($"Travel to {zone}", $"{WayfinderIpc.CleanLabel(a.Name)} is not attuned; a usable connection is not known", false),
            GuideKind.Elsewhere => new($"Travel to {zone}", "No usable teleport or connecting transport is known yet", false),
            GuideKind.Arrived => new($"Arrived at {destination}", "Complete the interaction here; arrival does not complete the objective", false),
            GuideKind.Walk when hasPath => new($"Follow the path to {destination}", "The trail follows the current walkable route", true),
            GuideKind.Walk => new($"Destination: {destination}",
                $"No walkable route yet: {WayfinderIpc.CleanLabel(string.IsNullOrWhiteSpace(routeNote) ? "route unavailable" : routeNote)}", false),
            _ => new("Choose a destination", "Select a quest, map flag or saved place", false),
        };
    }
}
