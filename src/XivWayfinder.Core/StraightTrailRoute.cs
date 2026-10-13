using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>
/// Keeps an explicitly requested straight-line trail fixed in the world as the player walks along it.
/// This is not a walkable route. A changed destination, floor or meaningful departure from the line creates
/// a new line; normal movement along it only changes which fixed samples are ahead of the player.
/// </summary>
public sealed class StraightTrailRoute
{
    private Vector3[] path = [];
    private uint territory;
    private bool hasHeight;

    public Vector3[] Update(Vector3 player, Target target)
    {
        var destination = target.Position(player.Y);
        if (!TrailPlacement.IsFinite(player) || !TrailPlacement.IsFinite(destination)) { Reset(); return path; }
        var changed = path.Length != 2 || territory != target.TerritoryId || hasHeight != target.Y.HasValue;
        if (!changed)
        {
            var arc = PathTrail.Project(path, player, out var away);
            var expected = PathTrail.At(path, arc);
            changed = Heading.FlatDistance(path[1], destination) > 0.5f || away > 0.75f ||
                MathF.Abs(player.Y - expected.Y) > 0.75f ||
                (target.Y.HasValue && MathF.Abs(path[1].Y - destination.Y) > 0.5f);
        }
        if (changed)
        {
            path = [player, destination];
            territory = target.TerritoryId;
            hasHeight = target.Y.HasValue;
        }
        return path;
    }

    public void Reset() => path = [];
}
