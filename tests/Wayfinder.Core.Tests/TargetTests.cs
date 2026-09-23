using System.Numerics;

namespace Wayfinder.Core.Tests;

/// <summary>Target priority, the plan for each kind of hint, the aetheryte choice, arrival, visibility and quests.</summary>
public sealed class TargetTests
{
    private static readonly Target Mine = new(TargetSource.Explicit, 132, 10, 10, null, "mine");
    private static readonly Target Flag = new(TargetSource.Flag, 132, 50, 50, null, "map flag");
    private static readonly Target Quest = new(TargetSource.Quest, 148, -20, 5, 3, "Quest");

    [Fact]
    public void AutoTakesExplicitThenFlagThenQuest()
    {
        var all = SourceToggles.All;
        Assert.Same(Mine, TargetPicker.Pick(SourceMode.Auto, all, Mine, Flag, Quest));
        Assert.Same(Flag, TargetPicker.Pick(SourceMode.Auto, all, null, Flag, Quest));
        Assert.Same(Quest, TargetPicker.Pick(SourceMode.Auto, all, null, null, Quest));
        Assert.Null(TargetPicker.Pick(SourceMode.Auto, all, null, null, null));
    }

    [Fact]
    public void ASingleSourceModeTakesOnlyThatSource()
    {
        var all = SourceToggles.All;
        Assert.Same(Flag, TargetPicker.Pick(SourceMode.Flag, all, Mine, Flag, Quest));
        Assert.Same(Quest, TargetPicker.Pick(SourceMode.Quest, all, Mine, Flag, Quest));
        Assert.Null(TargetPicker.Pick(SourceMode.Flag, all, Mine, null, Quest));
        Assert.Same(Mine, TargetPicker.Pick(SourceMode.Explicit, all, Mine, Flag, Quest));
    }

    [Fact]
    public void ASwitchedOffSourceIsSkippedEvenInItsOwnMode()
    {
        var noFlag = new SourceToggles(true, false, true);
        Assert.Same(Quest, TargetPicker.Pick(SourceMode.Auto, noFlag, null, Flag, Quest));
        Assert.Null(TargetPicker.Pick(SourceMode.Flag, noFlag, Mine, Flag, Quest));
        var noExplicit = new SourceToggles(false, true, true);
        Assert.Same(Flag, TargetPicker.Pick(SourceMode.Auto, noExplicit, Mine, Flag, Quest));
    }

    [Fact]
    public void SameZoneWalksStraightAtTheTarget()
    {
        var g = TargetPicker.Plan(132, new Vector3(10, 0, 0), Mine, 4f, []);
        Assert.Equal(GuideKind.Walk, g.Kind);
        Assert.Equal(10f, g.Distance, 1e-4f);
        Assert.Equal(new Vector2(0, 1), g.Direction);
    }

    [Fact]
    public void APathCornerStearsTheDirectionButNotTheDistance()
    {
        var g = TargetPicker.Plan(132, new Vector3(10, 0, 0), Mine, 4f, [], aim: new Vector3(13, 0, 4));
        Assert.Equal(GuideKind.Walk, g.Kind);
        Assert.Equal(10f, g.Distance, 1e-4f);
        Assert.Equal(0.6f, g.Direction.X, 1e-4f);
        Assert.Equal(0.8f, g.Direction.Y, 1e-4f);
        // a corner right underfoot is ignored rather than spinning the pointer
        var g2 = TargetPicker.Plan(132, new Vector3(10, 0, 0), Mine, 4f, [], aim: new Vector3(10.1f, 0, 0.1f));
        Assert.Equal(new Vector2(0, 1), g2.Direction);
    }

    [Fact]
    public void WithinTheRadiusIsArrived()
    {
        var g = TargetPicker.Plan(132, new Vector3(12, 30, 11), Mine, 4f, []);
        Assert.Equal(GuideKind.Arrived, g.Kind);
        Assert.Equal(Vector2.Zero, g.Direction);
    }

    [Fact]
    public void AnotherZoneNamesTheNearestAttunedAetheryte()
    {
        AetheryteSpot[] spots =
        [
            new(8, "Far", 148, 300, 300, true),
            new(9, "Near but not attuned", 148, -18, 5, false),
            new(10, "Near", 148, -40, 30, true),
            new(11, "Other zone", 132, -20, 5, true),
        ];
        var g = TargetPicker.Plan(132, Vector3.Zero, Quest, 4f, spots);
        Assert.Equal(GuideKind.Teleport, g.Kind);
        Assert.Equal(10u, g.Aetheryte!.Id);
        Assert.Equal(0f, g.Distance);
    }

    [Fact]
    public void AnotherZoneWithoutAttunedAetherytesStillNamesOne()
    {
        AetheryteSpot[] spots = [new(3, "B", 148, 100, 0, false), new(2, "A", 148, -100, 0, false)];
        Assert.Equal(3u, TargetPicker.NearestAetheryte(Quest with { X = 90 }, spots)!.Id);
        Assert.Equal(GuideKind.Elsewhere, TargetPicker.Plan(132, Vector3.Zero, Quest, 4f, []).Kind);
    }

    [Fact]
    public void NoTargetIsNothing()
    {
        Assert.Same(Guide.Nothing, TargetPicker.Plan(132, Vector3.Zero, null, 4f, []));
    }

    [Fact]
    public void ArrivalNeedsAMomentInside()
    {
        var timer = new ArrivalTimer();
        Assert.False(timer.Update(true, 10.0, 1.5));
        Assert.False(timer.Update(true, 11.0, 1.5));
        Assert.True(timer.Update(true, 11.6, 1.5));
        // walking out resets it
        Assert.False(timer.Update(false, 12.0, 1.5));
        Assert.False(timer.Update(true, 12.1, 1.5));
        Assert.True(timer.Update(true, 13.7, 1.5));
    }

    [Fact]
    public void CutscenesAndHiddenUiAlwaysHide()
    {
        var rules = new VisibilityRules(true, false, false);
        var fine = new Situation(true, false, false, false, false, false, false);
        Assert.True(Visibility.Decide(fine, rules).Show);
        Assert.Equal("cutscene", Visibility.Decide(fine with { Cutscene = true }, rules).Reason);
        Assert.Equal("game UI hidden", Visibility.Decide(fine with { UiHidden = true }, rules).Reason);
        Assert.Equal("changing zone", Visibility.Decide(fine with { BetweenAreas = true }, rules).Reason);
        Assert.Equal("group pose", Visibility.Decide(fine with { GPose = true }, rules).Reason);
        Assert.Equal("no character", Visibility.Decide(fine with { HasPlayer = false }, rules).Reason);
        Assert.False(Visibility.Decide(fine, rules with { Enabled = false }).Show);
    }

    [Fact]
    public void CombatAndDutiesHideOnlyWhenAsked()
    {
        var fighting = new Situation(true, false, false, false, false, InCombat: true, InDuty: true);
        Assert.True(Visibility.Decide(fighting, new VisibilityRules(true, false, false)).Show);
        Assert.Equal("in combat", Visibility.Decide(fighting, new VisibilityRules(true, true, false)).Reason);
        Assert.Equal("in a duty", Visibility.Decide(fighting, new VisibilityRules(true, false, true)).Reason);
    }

    [Fact]
    public void TrackedQuestsComeFirstThenPriorityThenJournal()
    {
        QuestLead[] journal =
        [
            new(100, 1, false, -1, false, 0),
            new(200, 0, false, 1, false, 1),
            new(300, 2, true, -1, false, 2),   // hidden
            new(400, 0, false, -1, true, 3),   // priority
            new(500, 0, false, 0, false, 4),
            new(0, 0, false, -1, false, 5),    // empty slot
        ];
        Assert.Equal([500, 200, 400, 100], QuestPick.Order(journal).Select(q => (int)q.QuestId));
    }

    [Fact]
    public void MarkersNameAQuestEitherWay()
    {
        Assert.True(QuestPick.MatchesObjective(1234, 1234));
        Assert.True(QuestPick.MatchesObjective(0x10000 + 1234, 1234));
        Assert.False(QuestPick.MatchesObjective(1235, 1234));
        Assert.False(QuestPick.MatchesObjective(0, 0));
    }

    [Fact]
    public void TodoRowsForTheCurrentStep()
    {
        byte[] seqs = [1, 1, 2, 255, 0];
        Assert.Equal([0, 1], QuestPick.TodoRowsFor(1, seqs));
        Assert.Equal([3], QuestPick.TodoRowsFor(255, seqs));
        // no row for sequence 3: the next later one, which is the final step
        Assert.Equal([3], QuestPick.TodoRowsFor(3, seqs));
        Assert.Empty(QuestPick.TodoRowsFor(4, [1, 2]));
    }

    [Fact]
    public void QuestLocationPrefersTheNearestHere()
    {
        QuestLocation[] places =
        [
            new(148, 0, 0, 0, "elsewhere"),
            new(132, 100, 0, 100, "far"),
            new(132, 5, 0, 5, "near"),
            new(0, 1, 1, 1, "no zone"),
        ];
        Assert.Equal("near", QuestPick.Nearest(places, 132, Vector3.Zero)!.Value.Label);
        Assert.Equal("elsewhere", QuestPick.Nearest(places, 999, Vector3.Zero)!.Value.Label);
        Assert.Null(QuestPick.Nearest([], 132, Vector3.Zero));
    }
}
