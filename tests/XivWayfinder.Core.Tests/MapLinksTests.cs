using System.Numerics;
using XivWayfinder.Core;
using Xunit;

namespace XivWayfinder.Core.Tests;

public class MapLinksTests
{
    [Theory]
    [InlineData(MapLinks.FlagMarkerType, 0u, true, false, 0.3, MapLinkAction.FollowFlag)]  // a <flag> link: set a moment ago
    [InlineData(MapLinks.FlagMarkerType, 0u, true, false, 30.0, MapLinkAction.Open)]       // the map key with an old flag
    [InlineData(MapLinks.FlagMarkerType, 0u, true, false, -1.0, MapLinkAction.Open)]       // no flag seen
    [InlineData(MapLinks.QuestLogType, 1234u, true, false, 99.0, MapLinkAction.FollowQuest)]
    [InlineData(MapLinks.QuestLogType, 0u, true, false, 0.1, MapLinkAction.Open)]          // no quest: nothing to follow
    [InlineData(4u, 0u, true, false, 0.1, MapLinkAction.Open)]                             // Centered: just the map
    [InlineData(6u, 0u, true, false, 0.1, MapLinkAction.Open)]                             // Teleport
    [InlineData(MapLinks.FlagMarkerType, 0u, true, true, 0.3, MapLinkAction.Open)]         // Shift held
    [InlineData(MapLinks.QuestLogType, 1234u, false, false, 0.3, MapLinkAction.Open)]      // switched off
    public void WhichOpensAreFollowed(uint type, uint quest, bool enabled, bool bypass, double since, MapLinkAction want) =>
        Assert.Equal(want, MapLinks.Decide(type, quest, enabled, bypass, since));

    [Fact]
    public void QuestIdsFromEitherNumbering()
    {
        Assert.Equal((ushort)1234, MapLinks.JournalId(1234));
        Assert.Equal((ushort)1234, MapLinks.JournalId(QuestPick.QuestRowBase + 1234));
    }

    [Fact]
    public void AShownQuestComesFirst()
    {
        var leads = new[]
        {
            new QuestLead(10, 1, false, 0, false, 0),  // tracked
            new QuestLead(20, 1, false, -1, true, 1),  // priority
            new QuestLead(30, 1, false, -1, false, 2),
        };
        Assert.Equal(new ushort[] { 10, 20, 30 }, QuestPick.Order(leads).Select(q => q.QuestId));
        Assert.Equal(new ushort[] { 30, 10, 20 }, QuestPick.Order(leads, prefer: 30).Select(q => q.QuestId));
        Assert.Equal(new ushort[] { 10, 20, 30 }, QuestPick.Order(leads, prefer: 99).Select(q => q.QuestId));
    }
}

public class MinionLeadTests
{
    private static readonly MinionLayout Layout = MinionLayout.Default;

    [Fact]
    public void LeadsAlongTheRoute()
    {
        // a route east 10 yalms, then north 10: the glove floats 4 yalms along it, pointing on along it
        var path = new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 0, -10) };
        var lead = MinionGlove.Lead(new Vector3(2, 0, 0.5f), path, Vector2.UnitX, 4f, Layout, 0);
        Assert.NotNull(lead);
        var (at, dir) = lead!.Value;
        Assert.InRange(at.X, 5.9f, 6.1f);
        Assert.InRange(at.Z, -0.1f, 0.1f);
        Assert.InRange(at.Y, Layout.Height - 0.1f, Layout.Height + 0.1f);
        Assert.True(dir.X > 0.99f, "pointing on along the route");
        // round the corner: it waits on the next leg and points along it
        (at, dir) = MinionGlove.Lead(new Vector3(9, 0, 0), path, Vector2.UnitX, 4f, Layout, 0)!.Value;
        Assert.InRange(at.X, 9.9f, 10.1f);
        Assert.InRange(at.Z, -3.1f, -2.9f);
        Assert.True(dir.Y < -0.99f, "pointing north, the way the route turns");
    }

    [Fact]
    public void WaitsAtTheEnd()
    {
        var path = new[] { new Vector3(0, 0, 0), new Vector3(3, 0, 0) };
        var (at, dir) = MinionGlove.Lead(new Vector3(1, 0, 0), path, Vector2.UnitX, 4f, Layout, 0)!.Value;
        Assert.InRange(at.X, 2.9f, 3.1f);
        Assert.True(dir.X > 0.99f);
    }

    [Fact]
    public void StraightWithoutARoute()
    {
        var (at, dir) = MinionGlove.Lead(new Vector3(1, 2, 3), ReadOnlySpan<Vector3>.Empty, new Vector2(0, 2), 4f, Layout, 0)!.Value;
        Assert.Equal(new Vector2(0, 1), dir);
        Assert.InRange(at.Z, 6.9f, 7.1f);
        Assert.Null(MinionGlove.Lead(Vector3.Zero, ReadOnlySpan<Vector3>.Empty, Vector2.Zero, 4f, Layout, 0));
    }
}
