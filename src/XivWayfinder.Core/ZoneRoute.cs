using System.Numerics;
namespace XivWayfinder.Core;

public sealed record ZoneConnection(uint From, uint To, Vector3 Entrance, string Action)
{
    // ExitRange translations locate a trigger volume, not its walkable floor.
    // Preserve its map position but let the navigation mesh resolve altitude,
    // just as it does for a map flag. Keep the final objective's known Y intact.
    public Target WalkingTarget(Target destination) => new(destination.Source, From,
        Entrance.X, Entrance.Z, null, Action, destination.MainScenario);
}

/// <summary>Walkable zone transitions. Replan from the actual zone after every crossing.</summary>
public static class ZoneRoute
{
    public static ZoneConnection? Next(uint from, uint to, Vector3 player, IReadOnlyList<ZoneConnection> connections)
    {
        if (from == to) return null;
        // Reverse breadth-first distances prevent loops and always make progress toward the destination.
        var distance = new Dictionary<uint, int> { [to] = 0 };
        var queue = new Queue<uint>(); queue.Enqueue(to);
        while (queue.TryDequeue(out var zone))
            foreach (var edge in connections)
                if (edge.To == zone && !distance.ContainsKey(edge.From))
                { distance[edge.From] = distance[zone] + 1; queue.Enqueue(edge.From); }
        return connections.Where(e => e.From == from && distance.ContainsKey(e.To)
                && float.IsFinite(e.Entrance.X) && float.IsFinite(e.Entrance.Y) && float.IsFinite(e.Entrance.Z))
            .OrderBy(e => distance[e.To]).ThenBy(e => Vector3.DistanceSquared(player, e.Entrance)).FirstOrDefault();
    }
}
