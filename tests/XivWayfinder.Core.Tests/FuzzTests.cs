using System.Text;

namespace XivWayfinder.Core.Tests;

/// <summary>
/// The parsers that read text XivWayfinder did not write (what the player types, what other plugins send over IPC)
/// must refuse bad input, never throw. XIVWAYFINDER_FUZZ_SECONDS sets the budget (default 2, the nightly workflow
/// raises it); XIVWAYFINDER_FUZZ_SEED replays a run.
/// </summary>
public sealed class FuzzTests
{
    private static readonly string[] Corpus =
    [
        "11.2 14.5",
        "X: 11.2 Y: 14.5 Z: 0.3",
        "Central Shroud ( 21.5 , 22.1 )",
        "21.5 22.1 the central shroud",
        "test 35",
        "clear",
        "quest",
        "glove",
    ];

    private static readonly string[] Splices =
        ["", " ", "(", ")", ",", ":", "x:", "z:", "-", "+", ".", "1e9", "NaN", "\ud800", "\udc00", "\u0000", "​", "", "é", "🐾", "999999999999", "0", "42", "the "];

    private static readonly Zone[] Zones = [new(148, "Central Shroud"), new(132, "New Gridania"), new(129, "Limsa Lominsa Lower Decks")];

    [Fact]
    public void ParsersNeverThrow()
    {
        var seed = int.TryParse(Environment.GetEnvironmentVariable("XIVWAYFINDER_FUZZ_SEED"), out var s) ? s : Environment.TickCount;
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("XIVWAYFINDER_FUZZ_SECONDS"), out var b) ? b : 2;
        var rng = new Random(seed);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var runs = 0;
        while (DateTime.UtcNow < deadline || runs < 1000)
        {
            runs++;
            var text = Mutate(rng);
            try
            {
                var request = WayfinderCommands.Parse(text);
                if (request.Verb == Verb.MapTarget)
                {
                    Assert.True(MapCoords.IsPlausibleMapCoord(request.X) && MapCoords.IsPlausibleMapCoord(request.Y));
                    ZoneMatch.Find(request.Zone, Zones);
                }

                ZoneMatch.Find(text, Zones);
                var label = WayfinderIpc.CleanLabel(text);
                Assert.True(label.Length <= WayfinderIpc.MaxLabel);
                var x = (float)(rng.NextDouble() * 20000 - 10000);
                var target = WayfinderIpc.Validate((uint)rng.Next(0, 3), x, rng.Next(2) == 0 ? float.NaN : x, text, 132, out _);
                var state = new WayfinderState
                {
                    Reason = text,
                    ZoneName = text,
                    Guide = new Guide(GuideKind.Walk, target ?? new Target(TargetSource.Quest, 1, 0, 0, null, text), float.NaN, default, new AetheryteSpot(1, text, 1, 0, 0, false)),
                };
                using var doc = System.Text.Json.JsonDocument.Parse(WayfinderIpc.StateJson(state));
                _ = WayfinderIpc.StatusLine(state);
            }
            catch (Exception ex)
            {
                Assert.Fail($"seed {seed}: {ex.GetType().Name} for {Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}: {ex.Message}");
            }
        }
    }

    private static string Mutate(Random rng)
    {
        var sb = new StringBuilder(Corpus[rng.Next(Corpus.Length)]);
        var edits = rng.Next(1, 6);
        for (var i = 0; i < edits; i++)
        {
            var at = sb.Length == 0 ? 0 : rng.Next(sb.Length + 1);
            switch (rng.Next(4))
            {
                case 0:
                    sb.Insert(at, Splices[rng.Next(Splices.Length)]);
                    break;
                case 1 when sb.Length > 0:
                    sb.Remove(Math.Min(at, sb.Length - 1), 1);
                    break;
                case 2:
                    sb.Insert(at, (char)rng.Next(0, 0x10000));
                    break;
                default:
                    sb.Insert(at, rng.Next(-100, 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }

        return sb.Length > 600 ? sb.ToString(0, 600) : sb.ToString();
    }
}
