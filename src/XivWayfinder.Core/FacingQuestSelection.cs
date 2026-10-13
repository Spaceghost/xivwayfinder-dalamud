using System.Numerics;

namespace XivWayfinder.Core;

public readonly record struct FacingQuest(QuestFound Quest, float AngleDegrees, float Distance);

/// <summary>
/// Deliberate destination selection, not a navigation direction. Called only on
/// a user's selection action; turning during ordinary play never changes quests.
/// Chosen destinations still require the normal navmesh route before guidance.
/// </summary>
public static class FacingQuestSelection
{
    public const float ConeDegrees = 25;

    public static FacingQuest? Choose(IReadOnlyList<QuestFound> candidates, uint territory,
        Vector3 player, float rotation, float arrivalRadius = 2)
    {
        if (territory == 0 || !Finite(player) || !float.IsFinite(rotation)
            || !float.IsFinite(arrivalRadius) || arrivalRadius < 0) return null;
        var forward = Heading.Forward(rotation);
        var minimum = Math.Max(.25f, arrivalRadius);
        FacingQuest? best = null;
        foreach (var candidate in candidates)
        {
            var place = candidate.Place;
            var at = new Vector3(place.X, place.Y, place.Z);
            if (candidate.Lead.QuestId == 0 || candidate.Lead.Hidden
                || place.TerritoryId != territory || !Finite(at)) continue;
            var delta = Heading.Flat(player, at);
            var distance = delta.Length();
            if (!float.IsFinite(distance) || distance <= minimum) continue;
            var angle = MathF.Acos(Math.Clamp(Vector2.Dot(forward, delta / distance), -1, 1)) * (180 / MathF.PI);
            if (angle > ConeDegrees) continue;
            // Facing intent wins over proximity. Exact ties are deterministic,
            // independent of tracked order and incidental journal enumeration.
            if (best is not { } current || angle < current.AngleDegrees
                || angle == current.AngleDegrees && (distance < current.Distance
                    || distance == current.Distance && candidate.Lead.QuestId < current.Quest.Lead.QuestId))
                best = new(candidate, angle, distance);
        }
        return best;
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y)
        && float.IsFinite(p.Z) && Math.Abs(p.X) <= 1_000_000 && Math.Abs(p.Y) <= 1_000_000 && Math.Abs(p.Z) <= 1_000_000;
}
