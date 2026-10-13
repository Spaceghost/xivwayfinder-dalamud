using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public class AutoTeleportTests
{
    [Fact]
    public void IpcReportsOptInCostLimitAndSanitizedStatusWithoutChangingGuidance()
    {
        var state = new WayfinderState
        {
            AutoTeleportEnabled = true,
            AutoTeleportMaxGil = 1500,
            AutoTeleportStatus = "Waiting:\ncasting\u0000",
        };
        using var json = System.Text.Json.JsonDocument.Parse(WayfinderIpc.StateJson(state));
        var teleport = json.RootElement.GetProperty("autoTeleport");
        Assert.True(teleport.GetProperty("enabled").GetBoolean());
        Assert.Equal(1500u, teleport.GetProperty("maxGil").GetUInt32());
        Assert.Equal("Waiting: casting", teleport.GetProperty("status").GetString());
        Assert.Equal("none", json.RootElement.GetProperty("guide").GetString());
    }

    [Fact]
    public void TeleportSettingBudgetAndStatusChangesPublishStateEvents()
    {
        var state = new WayfinderState();
        var key = StateChangeKey.From(state);
        Assert.Equal(key, StateChangeKey.From(state with { Guide = Guide.Nothing with { Distance = 50 } }));
        Assert.NotEqual(key, StateChangeKey.From(state with { AutoTeleportEnabled = true }));
        Assert.NotEqual(key, StateChangeKey.From(state with { AutoTeleportMaxGil = 1500 }));
        Assert.NotEqual(key, StateChangeKey.From(state with { AutoTeleportStatus = "Waiting: casting." }));
    }

    private static readonly TeleportOffer Town = new(42, 0, 200, 500);

    private static AutoTeleportDecision Step(AutoTeleportPolicy policy, double time, TeleportOffer? offer = null,
        bool safe = true, bool enabled = true, uint origin = 100, uint cap = 2000) =>
        policy.Decide(enabled, origin, offer ?? Town, safe, cap, time);

    [Fact]
    public void RequiresStableIdleGuidanceAndConsumesExactlyOneAttempt()
    {
        var policy = new AutoTeleportPolicy();
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 0));
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 1.9));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 2));
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(policy, 2.001));
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(policy, 3600));
    }

    [Theory]
    [InlineData(false, true, 100, 2000, AutoTeleportDecision.Off)]
    [InlineData(true, false, 100, 2000, AutoTeleportDecision.Unsafe)]
    [InlineData(true, true, 0, 2000, AutoTeleportDecision.NoDestination)]
    [InlineData(true, true, 200, 2000, AutoTeleportDecision.NoDestination)]
    [InlineData(true, true, 100, 499, AutoTeleportDecision.OverBudget)]
    public void GuardsDoNotConsumeAnAttempt(bool enabled, bool safe, uint origin, uint cap, AutoTeleportDecision expected)
    {
        var policy = new AutoTeleportPolicy();
        Assert.Equal(expected, Step(policy, 0, enabled: enabled, safe: safe, origin: origin, cap: cap));
        Assert.Equal(expected, Step(policy, 10, enabled: enabled, safe: safe, origin: origin, cap: cap));
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 11));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 13));
    }

    [Fact]
    public void FreeOnlyBudgetAndExactLimitAreSupported()
    {
        var policy = new AutoTeleportPolicy();
        var free = Town with { GilCost = 0 };
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 0, free, cap: 0));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 2, free, cap: 0));
        policy = new();
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 0, cap: 500));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 2, cap: 500));
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(42, 0)]
    public void InvalidDestinationNeverCasts(uint id, uint territory)
    {
        var policy = new AutoTeleportPolicy();
        var offer = Town with { AetheryteId = id, TerritoryId = territory };
        Assert.Equal(AutoTeleportDecision.NoDestination, Step(policy, 0, offer));
        Assert.Equal(AutoTeleportDecision.NoDestination, Step(policy, 100, offer));
    }

    [Fact]
    public void AnyBusyIntervalRestartsTheIdleHold()
    {
        var policy = new AutoTeleportPolicy();
        Step(policy, 0);
        Assert.Equal(AutoTeleportDecision.Unsafe, Step(policy, 1, safe: false));
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 2));
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 3));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 4));
    }

    [Fact]
    public void QuoteAndDestinationChangesRestartTheHold()
    {
        var policy = new AutoTeleportPolicy();
        Step(policy, 0);
        var other = Town with { AetheryteId = 43 };
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 2, other));
        other = other with { GilCost = 550 };
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 4, other));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 6, other));
    }

    [Fact]
    public void MissingGuidanceDisableAndLoadingDoNotRetryAnAttempt()
    {
        var policy = new AutoTeleportPolicy();
        Step(policy, 0);
        Step(policy, 2);
        Assert.Equal(AutoTeleportDecision.NoDestination, policy.Decide(true, 100, null, true, 2000, 3));
        Assert.Equal(AutoTeleportDecision.Off, Step(policy, 4, enabled: false));
        Assert.Equal(AutoTeleportDecision.NoDestination, Step(policy, 5, origin: 0));
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(policy, 40));
    }

    [Fact]
    public void TargetFlappingCannotRepeatTheSameAetheryte()
    {
        var policy = new AutoTeleportPolicy();
        Step(policy, 0);
        Step(policy, 2);
        var other = Town with { AetheryteId = 43 };
        Step(policy, 3, other);
        Assert.Equal(AutoTeleportDecision.CoolingDown, Step(policy, 5, other));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 32, other));
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(policy, 100));
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(policy, 101, Town with { GilCost = 100 }));
    }

    [Fact]
    public void ExplicitRetryOnlyRearmsCurrentDestinationAndStillHonorsCooldown()
    {
        var policy = new AutoTeleportPolicy();
        Step(policy, 0);
        Assert.False(policy.Retry());
        Step(policy, 1);
        Step(policy, 3);
        Assert.True(policy.Retry());
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 4));
        Assert.Equal(AutoTeleportDecision.CoolingDown, Step(policy, 6));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 33));
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(policy, 100));
    }

    [Fact]
    public void NewTerritoryRearmsDestinationsWithoutBypassingGlobalCooldown()
    {
        var policy = new AutoTeleportPolicy();
        Step(policy, 0);
        Step(policy, 2);
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 4, origin: 101));
        Assert.Equal(AutoTeleportDecision.CoolingDown, Step(policy, 6, origin: 101));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 32, origin: 101));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidTimeIsNotAnAction(double time)
    {
        var policy = new AutoTeleportPolicy();
        Assert.Equal(AutoTeleportDecision.Unsafe, Step(policy, time));
    }

    [Theory]
    [InlineData("autoteleport on", "on")]
    [InlineData("autoteleport off", "off")]
    [InlineData("autoteleport retry", "retry")]
    [InlineData("AUTOTELEPORT ON", "on")]
    public void ExplicitCommandsParse(string command, string action)
    {
        var request = WayfinderCommands.Parse(command);
        Assert.Equal(Verb.AutoTeleport, request.Verb);
        Assert.Equal(action, request.Name);
    }

    [Theory]
    [InlineData("autoteleport")]
    [InlineData("autoteleport maybe")]
    [InlineData("autoteleport on 200")]
    public void AmbiguousTeleportCommandsAreRejected(string command) =>
        Assert.Equal(Verb.Invalid, WayfinderCommands.Parse(command).Verb);
}
