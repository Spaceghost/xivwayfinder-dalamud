namespace XivWayfinder.Core;

/// <summary>Bounded development terrain-search locations. The first pass keeps
/// half-yalm spacing; refinement fills only the missing quarter-yalm cells.</summary>
public static class TerrainCheckGrid
{
    public const float Spacing = .25f;
    public const float MinimumRadius = 1.75f;
    public const float MaximumRadius = 10;
    public static IReadOnlyList<(int X, int Z)> Coarse { get; } = Create(false);
    public static IReadOnlyList<(int X, int Z)> Refinement { get; } = Create(true);

    private static IReadOnlyList<(int X, int Z)> Create(bool refinement)
    {
        var cells = new List<(int X, int Z)>();
        var extent = (int)(MaximumRadius / Spacing);
        for (var x = -extent; x <= extent; x++) for (var z = -extent; z <= extent; z++)
        {
            var coarse = x % 2 == 0 && z % 2 == 0;
            if (coarse == refinement) continue;
            var distance = ((float)x * x + (float)z * z) * Spacing * Spacing;
            if (distance >= MinimumRadius * MinimumRadius && distance <= MaximumRadius * MaximumRadius)
                cells.Add((x, z));
        }
        cells.Sort(static (a, b) =>
        {
            var distance = (a.X * a.X + a.Z * a.Z).CompareTo(b.X * b.X + b.Z * b.Z);
            if (distance != 0) return distance;
            var x = a.X.CompareTo(b.X); return x != 0 ? x : a.Z.CompareTo(b.Z);
        });
        return cells.AsReadOnly();
    }
}
