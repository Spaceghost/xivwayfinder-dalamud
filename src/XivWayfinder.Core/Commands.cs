using System.Globalization;
using System.Text;

namespace XivWayfinder.Core;

/// <summary>What the pointer looks like.</summary>
public enum PointerStyle
{
    Bead,
    Glove,
    Both,

    /// <summary>
    /// The game's own Wind-up Cursor minion model floating beside the bead, pointing; the drawn glove still goes to
    /// the screen's edge when the way is off screen.
    /// </summary>
    Minion,
}

/// <summary>When the distance in yalms is written beside the pointer.</summary>
public enum DistanceMode
{
    Never,
    Hover,
    Always,
}

public enum Verb
{
    Open,
    Help,
    MapTarget,
    Clear,
    Test,
    Mode,
    Style,
    On,
    Off,
    Toggle,
    Status,
    Invalid,
}

/// <summary>One <c>/wayfinder</c> command, parsed.</summary>
public sealed record WayfinderRequest(Verb Verb)
{
    public float X { get; init; }
    public float Y { get; init; }
    public string Zone { get; init; } = "";
    public SourceMode Mode { get; init; }
    public PointerStyle Style { get; init; }
    public float Distance { get; init; } = WayfinderCommands.TestDistance;
    public string Error { get; init; } = "";
}

/// <summary>
/// <c>/wayfinder</c>'s arguments. Coordinates are the map coordinates the game prints, and pasting them the way
/// the game writes them works: "11.2 14.5", "(11.2, 14.5)", "X: 11.2 Y: 14.5 Z: 0.3" (the height is ignored),
/// or a chat map link's text "Central Shroud ( 21.5 , 22.1 )". Words that are not numbers name the zone.
/// </summary>
public static class WayfinderCommands
{
    public const float TestDistance = 20f;

    public const string Help =
        "/wayfinder X Y [zone] points at map coordinates (the zone defaults to where you are) · /wayfinder clear · " +
        "/wayfinder test (a target 20 yalms ahead) · /wayfinder auto|target|flag|quest chooses what to follow · " +
        "/wayfinder bead|glove|both|minion · /wayfinder on|off · /wayfinder status · /wayfinder opens the settings. /xivwayfinder works the same.";

    public static WayfinderRequest Parse(string? arguments)
    {
        var text = Clean(arguments ?? "");
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return new WayfinderRequest(Verb.Open);

        var first = tokens[0].ToLowerInvariant();
        if (tokens.Length == 1 || first == "test")
        {
            switch (first)
            {
                case "help" or "?":
                    return new WayfinderRequest(Verb.Help);
                case "clear" or "reset" or "stop":
                    return new WayfinderRequest(Verb.Clear);
                case "status" or "where":
                    return new WayfinderRequest(Verb.Status);
                case "on" or "show":
                    return new WayfinderRequest(Verb.On);
                case "off" or "hide":
                    return new WayfinderRequest(Verb.Off);
                case "toggle":
                    return new WayfinderRequest(Verb.Toggle);
                case "settings" or "config":
                    return new WayfinderRequest(Verb.Open);
                case "auto":
                    return new WayfinderRequest(Verb.Mode) { Mode = SourceMode.Auto };
                case "target" or "explicit":
                    return new WayfinderRequest(Verb.Mode) { Mode = SourceMode.Explicit };
                case "flag":
                    return new WayfinderRequest(Verb.Mode) { Mode = SourceMode.Flag };
                case "quest":
                    return new WayfinderRequest(Verb.Mode) { Mode = SourceMode.Quest };
                case "bead":
                    return new WayfinderRequest(Verb.Style) { Style = PointerStyle.Bead };
                case "glove" or "hand":
                    return new WayfinderRequest(Verb.Style) { Style = PointerStyle.Glove };
                case "both":
                    return new WayfinderRequest(Verb.Style) { Style = PointerStyle.Both };
                case "minion" or "cursor":
                    return new WayfinderRequest(Verb.Style) { Style = PointerStyle.Minion };
                case "test":
                {
                    if (tokens.Length == 1)
                        return new WayfinderRequest(Verb.Test);
                    if (tokens.Length == 2 && TryNumber(tokens[1], out var d) && d >= 3f && d <= 200f)
                        return new WayfinderRequest(Verb.Test) { Distance = d };
                    return Invalid("/wayfinder test [yalms]: a distance from 3 to 200");
                }
            }
        }

        return ParseCoordinates(tokens);
    }

    private static WayfinderRequest ParseCoordinates(string[] tokens)
    {
        var numbers = new List<float>(2);
        var zone = new List<string>();
        var skipNext = false;
        foreach (var raw in tokens)
        {
            if (skipNext)
            {
                skipNext = false;
                if (TryNumber(raw, out _))
                    continue;
            }

            var token = raw;
            var lower = token.ToLowerInvariant();
            if (lower is "x" or "x:" or "y" or "y:")
                continue;
            if (lower is "z" or "z:")
            {
                skipNext = true; // the height the game adds after Y
                continue;
            }

            if (lower.StartsWith("z:", StringComparison.Ordinal) && TryNumber(token[2..], out _))
                continue;
            if ((lower.StartsWith("x:", StringComparison.Ordinal) || lower.StartsWith("y:", StringComparison.Ordinal)) && token.Length > 2)
                token = token[2..];

            if (TryNumber(token, out var value))
            {
                if (numbers.Count == 2)
                    return Invalid("give two map coordinates, like /wayfinder 11.2 14.5");
                numbers.Add(value);
            }
            else
            {
                zone.Add(raw);
            }
        }

        if (numbers.Count == 0)
            return Invalid("unknown command. " + Help);
        if (numbers.Count != 2)
            return Invalid("give two map coordinates, like /wayfinder 11.2 14.5");
        if (!MapCoords.IsPlausibleMapCoord(numbers[0]) || !MapCoords.IsPlausibleMapCoord(numbers[1]))
            return Invalid("map coordinates run from 1 to about 42, as the game's map shows them");
        return new WayfinderRequest(Verb.MapTarget) { X = numbers[0], Y = numbers[1], Zone = string.Join(' ', zone) };
    }

    private static WayfinderRequest Invalid(string error) => new(Verb.Invalid) { Error = error };

    private static bool TryNumber(string token, out float value)
    {
        value = 0f;
        if (token.Length is 0 or > 16)
            return false;
        foreach (var c in token)
        {
            if (!(char.IsAsciiDigit(c) || c is '.' or '-' or '+'))
                return false;
        }

        return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
    }

    /// <summary>Punctuation the game puts around coordinates becomes spaces; the map-link glyph and control characters go.</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, 512));
        foreach (var c in text.AsSpan(0, Math.Min(text.Length, 512)))
        {
            if (c is '(' or ')' or ',' or '[' or ']' or '\t' or '\r' or '\n')
                sb.Append(' ');
            else if (char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.Format)
                sb.Append(' ');
            else
                sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}
