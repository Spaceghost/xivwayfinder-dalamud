using System.Numerics;
using System.Text.Json;
using XivWayfinder.Shared;

namespace XivWayfinder.Core.Tests;

/// <summary>/wayfinder's arguments, zone names, and the IPC checks and JSON.</summary>
public sealed class CommandAndIpcTests
{
    [Theory]
    [InlineData("11.2 14.5", 11.2f, 14.5f, "")]
    [InlineData("(11.2, 14.5)", 11.2f, 14.5f, "")]
    [InlineData("X: 11.2 Y: 14.5", 11.2f, 14.5f, "")]
    [InlineData("X: 11.2 Y: 14.5 Z: 0.3", 11.2f, 14.5f, "")]
    [InlineData("x:9 y:30.1 z:1.2", 9f, 30.1f, "")]
    [InlineData("21.5 22.1 central shroud", 21.5f, 22.1f, "central shroud")]
    [InlineData("Central Shroud ( 21.5  , 22.1 )", 21.5f, 22.1f, "Central Shroud")]
    [InlineData("\ue0bbCentral Shroud ( 21.5 , 22.1 )", 21.5f, 22.1f, "Central Shroud")]
    public void CoordinatesAsTheGameWritesThem(string args, float x, float y, string zone)
    {
        var r = WayfinderCommands.Parse(args);
        Assert.Equal(Verb.MapTarget, r.Verb);
        Assert.Equal(x, r.X, 1e-4f);
        Assert.Equal(y, r.Y, 1e-4f);
        Assert.Equal(zone, r.Zone);
    }

    [Theory]
    [InlineData("", Verb.Open)]
    [InlineData("  ", Verb.Open)]
    [InlineData("clear", Verb.Clear)]
    [InlineData("CLEAR", Verb.Clear)]
    [InlineData("status", Verb.Status)]
    [InlineData("test", Verb.Test)]
    [InlineData("on", Verb.On)]
    [InlineData("off", Verb.Off)]
    [InlineData("toggle", Verb.Toggle)]
    [InlineData("help", Verb.Help)]
    [InlineData("settings", Verb.Open)]
    public void Words(string args, Verb verb) => Assert.Equal(verb, WayfinderCommands.Parse(args).Verb);

    [Fact]
    public void ModesStylesAndTestDistance()
    {
        Assert.Equal(SourceMode.Flag, WayfinderCommands.Parse("flag").Mode);
        Assert.Equal(SourceMode.Quest, WayfinderCommands.Parse("quest").Mode);
        Assert.Equal(SourceMode.Explicit, WayfinderCommands.Parse("target").Mode);
        Assert.Equal(SourceMode.Auto, WayfinderCommands.Parse("auto").Mode);
        Assert.Equal(PointerStyle.Glove, WayfinderCommands.Parse("glove").Style);
        Assert.Equal(PointerStyle.Bead, WayfinderCommands.Parse("bead").Style);
        Assert.Equal(PointerStyle.Both, WayfinderCommands.Parse("both").Style);
        Assert.Equal(PointerStyle.Minion, WayfinderCommands.Parse("minion").Style);
        Assert.Equal(PointerStyle.Minion, WayfinderCommands.Parse("Cursor").Style);
        Assert.Equal(20f, WayfinderCommands.Parse("test").Distance);
        Assert.Equal(35f, WayfinderCommands.Parse("test 35").Distance);
        Assert.Equal(Verb.Invalid, WayfinderCommands.Parse("test 1").Verb);
        Assert.Equal(Verb.Invalid, WayfinderCommands.Parse("test far").Verb);
    }

    [Theory]
    [InlineData("11.2")]
    [InlineData("1 2 3")]
    [InlineData("0 14")]
    [InlineData("50 14")]
    [InlineData("NaN 12")]
    [InlineData("11.2 Infinity")]
    [InlineData("banana")]
    [InlineData("1e3 5")]
    public void NonsenseIsRefusedWithAReason(string args)
    {
        var r = WayfinderCommands.Parse(args);
        Assert.Equal(Verb.Invalid, r.Verb);
        Assert.NotEmpty(r.Error);
    }

    private static readonly Zone[] Zones =
    [
        new(132, "New Gridania"),
        new(148, "Central Shroud"),
        new(129, "Limsa Lominsa Lower Decks"),
        new(128, "Limsa Lominsa Upper Decks"),
        new(1052, "Central Shroud"),
        new(956, "Labyrinthos"),
        new(1187, "Urqopacha"),
        new(155, "Coerthas Central Highlands"),
    ];

    [Theory]
    [InlineData("central shroud", 148u)]
    [InlineData("Central  Shroud!", 148u)]
    [InlineData("the central shroud", 148u)]
    [InlineData("labyrinthos", 956u)]
    [InlineData("Limsa", 128u)]
    [InlineData("lower decks", 129u)]
    [InlineData("urqopachá", 1187u)]
    [InlineData("1052", 1052u)]
    public void ZoneNames(string query, uint id) => Assert.Equal(id, ZoneMatch.Find(query, Zones)!.Value.TerritoryId);

    [Theory]
    [InlineData("")]
    [InlineData("nowhere")]
    [InlineData("999")]
    [InlineData("!!!")]
    public void UnknownZones(string query) => Assert.Null(ZoneMatch.Find(query, Zones));

    [Fact]
    public void SetTargetChecksItsArguments()
    {
        var ok = WayfinderIpc.Validate(0, 12.5f, -300f, " the  ferry\tdock ", 132, out var error);
        Assert.NotNull(ok);
        Assert.Equal("", error);
        Assert.Equal(132u, ok.TerritoryId);
        Assert.Equal(TargetSource.Explicit, ok.Source);
        Assert.Equal("the ferry dock", ok.Label);
        Assert.Null(ok.Y);

        Assert.Equal(148u, WayfinderIpc.Validate(148, 0, 0, null, 132, out _)!.TerritoryId);
        Assert.Null(WayfinderIpc.Validate(0, 1, 1, "", 0, out error));
        Assert.NotEmpty(error);
        Assert.Null(WayfinderIpc.Validate(132, float.NaN, 1, "", 132, out _));
        Assert.Null(WayfinderIpc.Validate(132, 1, float.PositiveInfinity, "", 132, out _));
        Assert.Null(WayfinderIpc.Validate(132, 9000, 1, "", 132, out _));
    }

    [Fact]
    public void LabelsAreCleanedAndCapped()
    {
        Assert.Equal("", WayfinderIpc.CleanLabel(null));
        Assert.Equal("a b", WayfinderIpc.CleanLabel("a\u0000\u200Bb"));
        Assert.Equal("moogle 🐾 here", WayfinderIpc.CleanLabel("moogle 🐾 here"));
        Assert.Equal("broken", WayfinderIpc.CleanLabel("bro\ud800ken").Replace(" ", ""));
        Assert.Equal(WayfinderIpc.MaxLabel, WayfinderIpc.CleanLabel(new string('x', 500)).Length);
    }

    [Fact]
    public void StateJsonForAWalk()
    {
        var target = new Target(TargetSource.Flag, 148, 12.345f, -6.5f, null, "map \"flag\"");
        var state = new WayfinderState
        {
            Enabled = true,
            Visible = true,
            Reason = "showing",
            Mode = SourceMode.Auto,
            Style = PointerStyle.Glove,
            PlayerTerritoryId = 148,
            Guide = new Guide(GuideKind.Walk, target, 42.19f, new Vector2(0, 1), null),
            ZoneName = "Central Shroud",
            Navmesh = true,
        };
        using var doc = JsonDocument.Parse(WayfinderIpc.StateJson(state));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("walk", root.GetProperty("guide").GetString());
        Assert.Equal("auto", root.GetProperty("mode").GetString());
        Assert.Equal("glove", root.GetProperty("style").GetString());
        Assert.True(root.GetProperty("navmesh").GetBoolean());
        Assert.Equal("", root.GetProperty("routeNote").GetString());
        Assert.Equal(42.19, root.GetProperty("distance").GetDouble(), 3);
        var t = root.GetProperty("target");
        Assert.Equal("flag", t.GetProperty("source").GetString());
        Assert.Equal(148u, t.GetProperty("territoryId").GetUInt32());
        Assert.Equal(12.35, t.GetProperty("x").GetDouble(), 3);
        Assert.Equal(JsonValueKind.Null, t.GetProperty("y").ValueKind);
        Assert.Equal("map \"flag\"", t.GetProperty("label").GetString());
        Assert.Equal("Central Shroud", t.GetProperty("zone").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("aetheryte").ValueKind);
    }

    [Fact]
    public void StateJsonForATeleportAndForNothing()
    {
        var target = new Target(TargetSource.Explicit, 956, 1, 2, 3, "Ghostty link");
        var state = new WayfinderState
        {
            Guide = new Guide(GuideKind.Teleport, target, 0, Vector2.Zero, new AetheryteSpot(166, "The Archeion", 956, 0, 0, true)),
            ZoneName = "Labyrinthos",
        };
        using (var doc = JsonDocument.Parse(WayfinderIpc.StateJson(state)))
        {
            var root = doc.RootElement;
            Assert.Equal("teleport", root.GetProperty("guide").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("distance").ValueKind);
            Assert.Equal("target", root.GetProperty("target").GetProperty("source").GetString());
            Assert.Equal(3, root.GetProperty("target").GetProperty("y").GetDouble());
            Assert.Equal("The Archeion", root.GetProperty("aetheryte").GetProperty("name").GetString());
            Assert.True(root.GetProperty("aetheryte").GetProperty("attuned").GetBoolean());
        }

        using var none = JsonDocument.Parse(WayfinderIpc.StateJson(new WayfinderState()));
        Assert.Equal("none", none.RootElement.GetProperty("guide").GetString());
        Assert.Equal(JsonValueKind.Null, none.RootElement.GetProperty("target").ValueKind);
    }

    [Fact]
    public void StatusLineReadsNaturally()
    {
        var target = new Target(TargetSource.Explicit, 132, 0, 0, null, "test: 20 yalms ahead");
        var line = WayfinderIpc.StatusLine(new WayfinderState
        {
            Visible = true,
            Mode = SourceMode.Auto,
            Guide = new Guide(GuideKind.Walk, target, 19.6f, new Vector2(1, 0), null),
        });
        Assert.Equal("pointing at target \"test: 20 yalms ahead\": 20 yalms away · following auto · showing", line);
    }

    [Fact]
    public void StatusLineSaysWhetherItFollowsAPathOrAStraightLine()
    {
        var target = new Target(TargetSource.Flag, 132, 0, 0, null, "map flag");
        var walk = new Guide(GuideKind.Walk, target, 55f, new Vector2(1, 0), null);
        var path = WayfinderIpc.StatusLine(new WayfinderState { Visible = true, Guide = walk, Navmesh = true, RouteNote = "following a 6-corner path" });
        Assert.Contains("55 yalms away, along vnavmesh's path ·", path);
        var straight = WayfinderIpc.StatusLine(new WayfinderState { Visible = true, Guide = walk, RouteNote = "not installed" });
        Assert.Contains("55 yalms away, in a straight line (vnavmesh: not installed) ·", straight);
    }

    [Fact]
    public void IpcNamesAreTheDocumentedOnes()
    {
        Assert.Equal("XivWayfinder.v1.SetTarget", IpcContract.SetTarget);
        Assert.Equal("XivWayfinder.v1.SetMapTarget", IpcContract.SetMapTarget);
        Assert.Equal("XivWayfinder.v1.Clear", IpcContract.Clear);
        Assert.Equal("XivWayfinder.v1.GetState", IpcContract.GetState);
        // vnavmesh's, from its IPCProvider.cs; none of the ones that move the character
        Assert.All(new[] { IpcContract.NavIsReady, IpcContract.NavPathfindCancelable, IpcContract.NavNearestPoint },
            n => Assert.DoesNotMatch(@"\.(Path|SimpleMove)\.", n));
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "IPC.md"));
        foreach (var name in new[] { IpcContract.SetTarget, IpcContract.SetMapTarget, IpcContract.Clear, IpcContract.GetState })
            Assert.Contains("`" + name + "`", doc);
    }

    internal static string RepoRoot() =>
        typeof(CommandAndIpcTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().First(a => a.Key == "RepoRoot").Value!;
}
