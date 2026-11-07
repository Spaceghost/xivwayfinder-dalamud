namespace XivWayfinder.Core;

/// <summary>Ordered, persisted stops. Progress is explicit, never inferred from proximity.</summary>
public sealed class JourneyPlan
{
    public const int MaxStops = 64;
    public List<Target> Stops { get; set; } = [];
    public int Index { get; set; }
    public bool Running { get; set; }
    public Target? Current => Running && Index >= 0 && Index < Stops.Count ? Stops[Index] : null;

    public bool Add(Target? target)
    {
        if (!Valid(target) || Stops.Count >= MaxStops) return false;
        Stops.Add(target! with { Label = WayfinderIpc.CleanLabel(target!.Label) });
        return true;
    }

    public bool Resume()
    {
        Running = Index >= 0 && Index < Stops.Count;
        return Running;
    }

    public bool Advance()
    {
        if (Index < 0 || Index >= Stops.Count) return false;
        Index++;
        if (Index == Stops.Count) Running = false;
        return true;
    }

    public bool Move(int from, int to)
    {
        // Completed stops and the current active stop stay stable while moving
        // pending work around. Pause to reorder the current stop explicitly.
        var first = Running ? Index + 1 : Index;
        if (from < first || to < first || from >= Stops.Count || to >= Stops.Count) return false;
        var stop = Stops[from];
        Stops.RemoveAt(from);
        Stops.Insert(to, stop);
        return true;
    }

    public bool Back()
    {
        if (Index <= 0 || Index > Stops.Count) return false;
        Index--;
        // Restoring a stop does not unexpectedly resume a paused journey.
        return true;
    }

    public bool Remove(int index)
    {
        // Pause before removing the active destination. Completed history stays intact.
        if (index < Index || index >= Stops.Count || (Running && index == Index)) return false;
        Stops.RemoveAt(index);
        return true;
    }

    public void Sanitize()
    {
        var original = Stops ?? [];
        var oldIndex = Math.Clamp(Index, 0, original.Count);
        var clean = new List<Target>();
        var newIndex = 0;
        for (var i = 0; i < original.Count && clean.Count < MaxStops; i++)
        {
            if (!Valid(original[i])) continue;
            clean.Add(original[i] with { Label = WayfinderIpc.CleanLabel(original[i].Label) });
            if (i < oldIndex) newIndex++;
        }
        Stops = clean;
        Index = newIndex;
        Running &= Index < Stops.Count;
    }

    private static bool Valid(Target? t) => t is not null && t.TerritoryId != 0
        && Enum.IsDefined(t.Source) && MapCoords.IsPlausibleWorldCoord(t.X)
        && MapCoords.IsPlausibleWorldCoord(t.Z) && (t.Y is null || MapCoords.IsPlausibleWorldCoord(t.Y.Value));
}
