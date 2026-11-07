using System.Text.Json;
using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public class JourneyPlanTests
{
    [Fact]
    public void JourneyProgressNotifiesConsumersEvenForRepeatedDestinations()
    {
        var state = new WayfinderState { JourneyCount = 2, JourneyRunning = true };
        Assert.NotEqual(StateChangeKey.From(state), StateChangeKey.From(state with { JourneyIndex = 1 }));
        Assert.NotEqual(StateChangeKey.From(state), StateChangeKey.From(state with { JourneyRunning = false }));
    }

    private static Target Stop(string name) => new(TargetSource.Explicit, 10, 1, 2, 3, name);

    [Fact]
    public void StopsResumeAndAdvanceOnlyExplicitly()
    {
        var plan = new JourneyPlan();
        Assert.False(plan.Resume());
        Assert.True(plan.Add(Stop("A")));
        Assert.True(plan.Add(Stop("B")));
        Assert.Null(plan.Current);
        Assert.True(plan.Resume());
        Assert.Equal("A", plan.Current!.Label);
        plan.Running = false;
        Assert.Null(plan.Current);
        plan.Resume();
        Assert.Equal("A", plan.Current!.Label);
        Assert.True(plan.Advance());
        Assert.Equal("B", plan.Current!.Label);
        Assert.True(plan.Advance());
        Assert.False(plan.Running);
        Assert.False(plan.Resume());
        Assert.False(plan.Advance());
        Assert.True(plan.Add(Stop("C")));
        Assert.True(plan.Resume());
        Assert.Equal("C", plan.Current!.Label);
    }

    [Fact]
    public void PreviousStopRestoresProgressWithoutResumingPausedJourney()
    {
        var plan = new JourneyPlan();
        plan.Add(Stop("A"));
        Assert.False(plan.Back());
        plan.Resume();
        plan.Advance();
        Assert.True(plan.Back());
        Assert.False(plan.Running);
        Assert.True(plan.Resume());
        Assert.Equal("A", plan.Current!.Label);
    }

    [Fact]
    public void RemoveProtectsActiveStopAndHistory()
    {
        var plan = new JourneyPlan();
        foreach (var name in new[] { "A", "B", "C" }) plan.Add(Stop(name));
        plan.Resume();
        plan.Advance();
        Assert.False(plan.Remove(0));
        Assert.False(plan.Remove(1));
        Assert.True(plan.Remove(2));
        Assert.Equal("B", plan.Current!.Label);
        plan.Running = false;
        Assert.True(plan.Remove(1));
        Assert.False(plan.Resume());
        Assert.False(plan.Remove(-1));
        Assert.False(plan.Remove(99));
    }

    [Fact]
    public void ReorderingDoesNotChangeCurrentOrCompletedStop()
    {
        var plan = new JourneyPlan();
        foreach (var name in new[] { "A", "B", "C", "D" }) plan.Add(Stop(name));
        plan.Resume();
        plan.Advance();
        Assert.False(plan.Move(0, 2));
        Assert.False(plan.Move(1, 2));
        Assert.True(plan.Move(3, 2));
        Assert.Equal("B", plan.Current!.Label);
        Assert.Equal("D", plan.Stops[2].Label);
    }

    [Fact]
    public void InvalidPersistedStopsDoNotShiftProgressToEarlierWork()
    {
        var plan = new JourneyPlan { Stops = [Stop("A"), Stop("bad") with { X = float.NaN }, Stop("B")], Index = 2, Running = true };
        plan.Sanitize();
        Assert.Equal(1, plan.Index);
        Assert.Equal("B", plan.Current!.Label);
        var restored = JsonSerializer.Deserialize<JourneyPlan>(JsonSerializer.Serialize(plan))!;
        restored.Sanitize();
        Assert.Equal("B", restored.Current!.Label);
    }

    [Fact]
    public void BoundedQueueRejectsInvalidDestinations()
    {
        var plan = new JourneyPlan();
        Assert.False(plan.Add(null));
        Assert.False(plan.Add(Stop("bad") with { TerritoryId = 0 }));
        for (var i = 0; i < JourneyPlan.MaxStops; i++) Assert.True(plan.Add(Stop(i.ToString())));
        Assert.False(plan.Add(Stop("overflow")));
    }
}
