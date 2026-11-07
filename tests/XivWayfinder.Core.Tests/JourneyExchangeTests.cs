using System.Text.Json;
using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public class JourneyExchangeTests
{
    private const string Batch = """{"version":1,"stops":[{"territoryId":10,"x":1,"y":3,"z":2,"label":"Errand"}]}""";

    [Fact]
    public void AppendDoesNotReplaceOrStartThePlayersJourney()
    {
        var plan = new JourneyPlan();
        Assert.True(JourneyExchange.Append(plan, Batch));
        Assert.True(JourneyExchange.Append(plan, Batch));
        Assert.Equal(2, plan.Stops.Count);
        Assert.False(plan.Running);
        Assert.Equal(3f, plan.Stops[0].Y);
        using var doc = JsonDocument.Parse(JourneyExchange.Snapshot(plan));
        Assert.Equal(2, doc.RootElement.GetProperty("stops").GetArrayLength());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"stops\":[]}")]
    [InlineData("{\"version\":1,\"stops\":[]}")]
    [InlineData("{\"version\":1,\"stops\":[{\"territoryId\":0,\"x\":1,\"z\":2}]}")]
    public void InvalidInputLeavesQueueUnchanged(string? json)
    {
        var plan = new JourneyPlan();
        JourneyExchange.Append(plan, Batch);
        var before = JourneyExchange.Snapshot(plan);
        Assert.False(JourneyExchange.Append(plan, json));
        Assert.Equal(before, JourneyExchange.Snapshot(plan));
    }

    [Fact]
    public void InvalidLastStopDoesNotPartiallyAppend()
    {
        var plan = new JourneyPlan();
        var json = Batch.Replace("}]}", "},{\"territoryId\":10,\"x\":1,\"z\":2,\"y\":6000}]}");
        Assert.False(JourneyExchange.Append(plan, json));
        Assert.Empty(plan.Stops);
        Assert.False(JourneyExchange.Append(plan, new string(' ', 32769)));
    }

    [Fact]
    public void CapacityAndExplicitControlsAreRespected()
    {
        var plan = new JourneyPlan();
        for (var i = 0; i < JourneyPlan.MaxStops; i++) Assert.True(JourneyExchange.Append(plan, Batch));
        Assert.False(JourneyExchange.Append(plan, Batch));
        Assert.True(JourneyExchange.Control(plan, "resume"));
        Assert.True(plan.Running);
        Assert.True(JourneyExchange.Control(plan, "next"));
        Assert.Equal(1, plan.Index);
        Assert.True(JourneyExchange.Control(plan, "pause"));
        Assert.True(JourneyExchange.Control(plan, "previous"));
        Assert.False(plan.Running);
        Assert.False(JourneyExchange.Control(plan, "teleport"));
    }
}
