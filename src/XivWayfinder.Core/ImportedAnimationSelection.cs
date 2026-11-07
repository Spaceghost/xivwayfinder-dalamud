namespace XivWayfinder.Core;

/// <summary>Selects recognizable locomotion loops. One generically named clip may animate movement;
/// unrecognized named actions and the stationary rest pose require no guessed clip.</summary>
public static class ImportedAnimationSelection
{
    // A loaded model exposes one immutable name list. Weak caching keeps frame
    // selection allocation-free after the first call without retaining old models.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<string>, HashSet<string>[]> Catalogs = new();
    private static readonly string[] Idle = ["idle", "rest", "stand", "survey", "breathe", "breathing", "relax"];
    private static readonly string[] FloatingIdle = ["idle", "hover", "float", "rest", "survey", "fly", "flight", "swim", "glide"];
    private static readonly string[] Walk = ["walk", "move", "locomotion", "jog", "run"];
    private static readonly string[] Run = ["run", "sprint", "jog", "walk", "move", "locomotion"];
    private static readonly string[] Fly = ["fly", "flight", "glide", "swim", "hover", "float", "move", "locomotion"];
    private static readonly HashSet<string> NotLoops = new(StringComparer.Ordinal)
    {"to","start","stop","end","transition","enter","exit","begin","finish","back","backward","backwards","left","right","strafe"};

    /// <returns>The matching clip index, or -1 for the authored rest pose. Explicit choices are never inferred.</returns>
    public static int Choose(IReadOnlyList<string> names, string? selected, float speed, bool grounded)
    {
        if (!string.IsNullOrWhiteSpace(selected))
            for (var i = 0; i < names.Count; i++)
                if (string.Equals(names[i], selected, StringComparison.Ordinal)) return i;
        var tokens = Catalogs.GetValue(names, static source => source.Select(Words).ToArray());
        if (float.IsFinite(speed) && speed > .15f)
        {
            var moving = Find(tokens, grounded ? speed > 3 ? Run : Walk : Fly);
            if (moving >= 0) return moving;
            // The importer names an unnamed clip "Animation 1". A single generic
            // clip is useful during movement, but never assume it is an idle or
            // pick one arbitrarily from several clips. Authored action names stay manual.
            if (tokens.Length == 1 && IsGenericAnimation(tokens[0])) return 0;
        }
        return Find(tokens, grounded ? Idle : FloatingIdle);
    }

    private static bool IsGenericAnimation(HashSet<string> words)
    {
        var animation = false;
        foreach (var word in words)
        {
            if (Matches(word, "animation")) animation = true;
            else if (!AllDigits(word)) return false;
        }
        return animation;
    }

    private static int Find(HashSet<string>[] tokens, string[] preferred)
    {
        foreach (var keyword in preferred)
            for (var i = 0; i < tokens.Length; i++)
                if (!tokens[i].Overlaps(NotLoops))
                    foreach (var token in tokens[i]) if (Matches(token, keyword)) return i;
        return -1;
    }

    private static bool Matches(string token, string keyword) => token == keyword ||
        token.StartsWith(keyword, StringComparison.Ordinal) && token.Length > keyword.Length && AllDigits(token.AsSpan(keyword.Length));

    private static bool AllDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value) if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    private static HashSet<string> Words(string? name)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(name)) return words;
        var start = 0;
        for (var i = 0; i <= name.Length; i++)
        {
            var delimiter = i == name.Length || !char.IsLetterOrDigit(name[i]);
            var camel = !delimiter && i > start && char.IsUpper(name[i]) &&
                (char.IsLower(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]));
            if (!delimiter && !camel) continue;
            if (i > start) words.Add(Canonical(name[start..i].ToLowerInvariant()));
            start = delimiter ? i + 1 : i;
        }
        return words;
    }

    private static string Canonical(string word) => word switch
    {
        "walking" => "walk",
        "running" => "run",
        "swimming" => "swim",
        "standing" => "stand",
        "moving" => "move",
        "flying" => "fly",
        "hovering" => "hover",
        "floating" => "float",
        "resting" => "rest",
        "relaxing" => "relax",
        "sprinting" => "sprint",
        "jogging" => "jog",
        "gliding" => "glide",
        "breathing" => "breathe",
        _ => word,
    };
}
