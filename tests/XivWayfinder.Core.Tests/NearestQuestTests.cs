using System.Numerics;
using XivWayfinder.Core;
using Xunit;

namespace XivWayfinder.Core.Tests;

public class NearestQuestTests
{
    private const uint Here = 132;
    private const uint Elsewhere = 133;
    private static readonly Vector3 Player = new(0f, 10f, 0f);

    private static QuestFound Q(ushort id, uint zone, float x, float z, int tracked = -1) =>
        new(new QuestLead(id, 1, false, tracked, false, id), new QuestLocation(zone, x, 0f, z, "q" + id));

    // in the tracked order: tracked first, far away; then two here, the second nearer; then one elsewhere
    private static readonly QuestFound[] Found =
    [
        Q(1, Here, 300f, 0f, tracked: 0),
        Q(2, Here, 40f, 30f),
        Q(3, Here, -10f, 5f),
        Q(4, Elsewhere, 1f, 1f),
    ];

    [Fact]
    public void NearestInYourZone() =>
        Assert.Equal((ushort)3, QuestPick.Choose(Found, QuestChoice.Nearest, 0, Here, Player)!.Value.Lead.QuestId);

    [Fact]
    public void AnotherZoneCountsOnlyWithNothingHere()
    {
        // the step in the other zone is 1.4 yalms "away" in its own coordinates: never compared with these
        Assert.NotEqual((ushort)4, QuestPick.Choose(Found, QuestChoice.Nearest, 0, Here, Player)!.Value.Lead.QuestId);
        Assert.Equal((ushort)1, QuestPick.Choose(Found, QuestChoice.Nearest, 0, 999, Player)!.Value.Lead.QuestId);
    }

    [Fact]
    public void TrackedTakesTheFirst() =>
        Assert.Equal((ushort)1, QuestPick.Choose(Found, QuestChoice.Tracked, 0, Here, Player)!.Value.Lead.QuestId);

    [Fact]
    public void AShownQuestWinsEitherWay()
    {
        Assert.Equal((ushort)2, QuestPick.Choose(Found, QuestChoice.Nearest, 2, Here, Player)!.Value.Lead.QuestId);
        Assert.Equal((ushort)4, QuestPick.Choose(Found, QuestChoice.Tracked, 4, Here, Player)!.Value.Lead.QuestId);
        Assert.Null(QuestPick.Choose([], QuestChoice.Nearest, 0, Here, Player));
    }

    [Fact]
    public void MainScenarioByTheGuidesIds()
    {
        ReadOnlySpan<ushort> msq = [4591, 0, 0];
        Assert.True(QuestPick.IsMainScenario(4591, msq));
        Assert.False(QuestPick.IsMainScenario(4592, msq));
        Assert.False(QuestPick.IsMainScenario(0, [0, 0, 0]));
        Assert.True(QuestPick.IsMainScenario(4591, [0, 0, 4591]), "any of the three paths");
    }

    [Fact]
    public void MainScenarioColours()
    {
        var mine = new Vector4(0.2f, 0.4f, 1f, 1f);
        var msq = new Target(TargetSource.Quest, Here, 0, 0, null, "q", MainScenario: true);
        var side = msq with { MainScenario = false };
        Assert.Equal(Colors.MainScenario, Colors.For(msq, mine, true));
        Assert.Equal(mine, Colors.For(msq, mine, false));
        Assert.Equal(mine, Colors.For(side, mine, true));
        Assert.Equal(mine, Colors.For(null, mine, true));
    }

    [Fact]
    public void TheGloveIsEagerOnTheMainScenario()
    {
        var path = new[] { new Vector3(0, 0, 0), new Vector3(20, 0, 0) };
        var calm = MinionGlove.Lead(new Vector3(1, 0, 0), path, Vector2.UnitX, 4f, MinionLayout.Default, 0.3)!.Value;
        var eager = MinionGlove.Lead(new Vector3(1, 0, 0), path, Vector2.UnitX, 4f, MinionLayout.Default, 0.3, eager: true)!.Value;
        Assert.InRange(eager.At.X - calm.At.X, MinionGlove.EagerExtra - 0.05f, MinionGlove.EagerExtra + 0.05f);
        // and bounces higher
        float Range(bool e)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (var t = 0.0; t < 3; t += 0.05)
            {
                var y = MinionGlove.Lead(new Vector3(1, 0, 0), path, Vector2.UnitX, 4f, MinionLayout.Default, t, e)!.Value.At.Y;
                lo = MathF.Min(lo, y);
                hi = MathF.Max(hi, y);
            }

            return hi - lo;
        }

        Assert.True(Range(true) > Range(false) * 1.5f, "an eager bounce");
    }
}
