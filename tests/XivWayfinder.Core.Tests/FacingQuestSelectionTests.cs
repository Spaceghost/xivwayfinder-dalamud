using System.Numerics;
using Xunit;

namespace XivWayfinder.Core.Tests;

public sealed class FacingQuestSelectionTests
{
    private static QuestFound Quest(ushort id, float x, float z, uint territory = 129) =>
        new(new(id, 1, false, -1, false, id), new(territory, x, 0, z, $"quest {id}"));

    [Fact]
    public void FacingIntentBeatsNearestAndTrackedQuest()
    {
        var close = Quest(2, 3, 8);
        close = close with { Lead = close.Lead with { TrackedOrder = 0, Priority = true } };
        var chosen = FacingQuestSelection.Choose([close, Quest(1, 0, 100)], 129, Vector3.Zero, 0);
        Assert.Equal((ushort)1, chosen!.Value.Quest.Lead.QuestId);
        Assert.Equal(0, chosen.Value.AngleDegrees);
    }

    [Theory]
    [InlineData(0, 0, 10)]
    [InlineData(1.5707963f, 10, 0)]
    [InlineData(-1.5707963f, -10, 0)]
    [InlineData(3.1415927f, 0, -10)]
    public void UsesCharacterRotationNotCameraRotation(float rotation, float x, float z)
    {
        Assert.NotNull(FacingQuestSelection.Choose([Quest(1, x, z)], 129, Vector3.Zero, rotation));
        Assert.Null(FacingQuestSelection.Choose([Quest(1, -x, -z)], 129, Vector3.Zero, rotation));
    }

    [Fact]
    public void HiddenOtherZoneArrivedAndMalformedDestinationsAreNotSelected()
    {
        var hidden = Quest(1, 0, 9);
        hidden = hidden with { Lead = hidden.Lead with { Hidden = true } };
        Assert.Null(FacingQuestSelection.Choose([hidden, Quest(0, 0, 9), Quest(2, 0, 9, 130),
            Quest(3, 0, 1), Quest(4, float.NaN, 9), Quest(5, 0, 2_000_000)], 129, Vector3.Zero, 0));
    }

    [Fact]
    public void ExactTiesAreStableWhenTheJournalOrderChanges()
    {
        QuestFound[] candidates = [Quest(2, 0, 12), Quest(1, 0, 12), Quest(3, 0, 30)];
        Assert.Equal((ushort)1, FacingQuestSelection.Choose(candidates, 129, Vector3.Zero, 0)!.Value.Quest.Lead.QuestId);
        Assert.Equal((ushort)1, FacingQuestSelection.Choose(candidates.Reverse().ToArray(), 129, Vector3.Zero, 0)!.Value.Quest.Lead.QuestId);
    }

    [Fact]
    public void MissingSelectionDoesNotInventADestination()
    {
        Assert.Null(FacingQuestSelection.Choose([], 129, Vector3.Zero, 0));
        Assert.Null(FacingQuestSelection.Choose([Quest(1, 8, 8)], 129, Vector3.Zero, 0));
        Assert.Null(FacingQuestSelection.Choose([Quest(1, 0, 20)], 0, Vector3.Zero, 0));
        Assert.Null(FacingQuestSelection.Choose([Quest(1, 0, 20)], 129, new(float.NaN), 0));
        Assert.Null(FacingQuestSelection.Choose([Quest(1, 0, 20)], 129, Vector3.Zero, float.NaN));
        Assert.Null(FacingQuestSelection.Choose([Quest(1, 0, 20)], 129, Vector3.Zero, 0, -1));
    }

    [Fact]
    public void SelectionIsExplicitAndDoesNotReplaceQuestMode()
    {
        Assert.Equal(Verb.FaceQuest, WayfinderCommands.Parse("face").Verb);
        Assert.Equal(Verb.FaceQuest, WayfinderCommands.Parse(" FACE ").Verb);
        Assert.Equal(Verb.Invalid, WayfinderCommands.Parse("face nearest").Verb);
        Assert.Equal(Verb.Mode, WayfinderCommands.Parse("quest").Verb);
        Assert.Equal(SourceMode.Quest, WayfinderCommands.Parse("quest").Mode);
    }
}
