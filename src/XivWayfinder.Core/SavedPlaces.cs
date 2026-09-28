using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>A player-named world position. Housing is refused by the plugin because territory alone cannot name a ward or plot.</summary>
public sealed record SavedPlace(string Name, uint TerritoryId, float X, float Y, float Z);

/// <summary>Validation and accent/case-insensitive lookup for the named places kept in plugin configuration.</summary>
public static class SavedPlaces
{
    public const int MaxCount = 100;
    public const int MaxName = 48;

    public static string CleanName(string? name)
    {
        var clean = WayfinderIpc.CleanLabel(name);
        if (clean.Length <= MaxName)
            return clean;
        var end = MaxName;
        if (char.IsHighSurrogate(clean[end - 1]))
            end--;
        return clean[..end].TrimEnd();
    }

    public static int Find(IReadOnlyList<SavedPlace> places, string? name)
    {
        var folded = ZoneMatch.Fold(CleanName(name));
        if (folded.Length == 0)
            return -1;
        for (var i = 0; i < places.Count; i++)
        {
            if (ZoneMatch.Fold(places[i].Name) == folded)
                return i;
        }

        return -1;
    }

    /// <summary>Adds or replaces a place. False means invalid data or a full book; <paramref name="error"/> explains which.</summary>
    public static bool Store(List<SavedPlace> places, SavedPlace place, out bool replaced, out string error)
    {
        replaced = false;
        var clean = CleanName(place.Name);
        if (clean.Length == 0)
        {
            error = "the place needs a name";
            return false;
        }

        if (!ValidPosition(place.TerritoryId, new Vector3(place.X, place.Y, place.Z)))
        {
            error = "the current position is not a valid world position";
            return false;
        }

        var normalized = place with { Name = clean };
        var index = Find(places, clean);
        if (index >= 0)
        {
            places[index] = normalized;
            replaced = true;
            error = "";
            return true;
        }

        if (places.Count >= MaxCount)
        {
            error = $"the place book is full ({MaxCount}); forget one first";
            return false;
        }

        places.Add(normalized);
        error = "";
        return true;
    }

    public static bool Forget(List<SavedPlace> places, string? name)
    {
        var index = Find(places, name);
        if (index < 0)
            return false;
        places.RemoveAt(index);
        return true;
    }

    /// <summary>Cleans persisted configuration, dropping invalid rows and keeping the last duplicate name.</summary>
    public static List<SavedPlace> Sanitize(IEnumerable<SavedPlace>? source)
    {
        var result = new List<SavedPlace>();
        if (source is null)
            return result;
        foreach (var place in source)
        {
            if (place is null)
                continue;
            var clean = CleanName(place.Name);
            if (clean.Length == 0 || !ValidPosition(place.TerritoryId, new Vector3(place.X, place.Y, place.Z)))
                continue;
            var normalized = place with { Name = clean };
            var old = Find(result, clean);
            if (old >= 0)
                result[old] = normalized;
            else if (result.Count < MaxCount)
                result.Add(normalized);
        }

        return result;
    }

    private static bool ValidPosition(uint territoryId, Vector3 position) =>
        territoryId != 0 && MapCoords.IsPlausibleWorldCoord(position.X) && MapCoords.IsPlausibleWorldCoord(position.Y) && MapCoords.IsPlausibleWorldCoord(position.Z);
}
