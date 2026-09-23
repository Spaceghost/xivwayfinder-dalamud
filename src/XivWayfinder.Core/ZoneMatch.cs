using System.Globalization;
using System.Text;

namespace XivWayfinder.Core;

/// <summary>A zone the player can name: a <c>TerritoryType</c> row and its place name.</summary>
public readonly record struct Zone(uint TerritoryId, string Name);

/// <summary>
/// Finds the zone a player means from what they typed: its territory id, or its name exactly, then a name that
/// starts with the words, then one that contains them. Case, accents, "the" and punctuation do not matter. Among
/// equally good matches the lowest territory id wins, which is the zone's original (field) copy.
/// </summary>
public static class ZoneMatch
{
    public static Zone? Find(string query, IEnumerable<Zone> zones)
    {
        var want = Fold(query);
        if (want.Length == 0)
            return null;
        var numeric = uint.TryParse(query.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id);

        Zone? best = null;
        var bestRank = int.MaxValue;
        foreach (var zone in zones)
        {
            int rank;
            if (numeric)
            {
                if (zone.TerritoryId != id)
                    continue;
                rank = 0;
            }
            else
            {
                var name = Fold(zone.Name);
                if (name.Length == 0)
                    continue;
                if (name == want)
                    rank = 1;
                else if (name.StartsWith(want, StringComparison.Ordinal))
                    rank = 2;
                else if (name.Contains(want, StringComparison.Ordinal))
                    rank = 3;
                else
                    continue;
            }

            if (rank < bestRank || (rank == bestRank && best is { } b && zone.TerritoryId < b.TerritoryId))
            {
                best = zone;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>Lower case, no accents, no punctuation, single spaces, and no leading "the".</summary>
    public static string Fold(string text)
    {
        string decomposed;
        try
        {
            decomposed = (text ?? "").Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            decomposed = text ?? ""; // a lone surrogate: not a name anyone typed, compared as it is
        }

        var sb = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            if (char.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
            {
                if (space && sb.Length > 0)
                    sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else
            {
                space = true;
            }
        }

        var folded = sb.ToString();
        return folded.StartsWith("the ", StringComparison.Ordinal) ? folded[4..] : folded;
    }
}
