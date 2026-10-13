using System.Text.Json;
using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public sealed class AutoTeleportPersistenceTests
{
    private const double Epoch = 1_800_000_000;
    private static readonly TeleportOffer Town = new(42, 0, 200, 500);
    private static readonly TeleportOffer Other = new(43, 0, 201, 600);

    private static AutoTeleportDecision Step(AutoTeleportPolicy policy, double now, TeleportOffer? offer = null,
        uint origin = 100, double? utc = null, bool safe = true, bool enabled = true) =>
        policy.Decide(enabled, origin, offer ?? Town, safe, 2000, now, utc ?? Epoch + now);

    private static void Consume(AutoTeleportPolicy policy)
    {
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 0));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 2));
    }

    private static AutoTeleportState Clone(AutoTeleportState state) =>
        AutoTeleportState.Decode(JsonSerializer.Serialize(state));

    [Theory]
    [InlineData(false)] // Game refused the cast.
    [InlineData(true)]  // Game started it, then it was interrupted before changing territory.
    public void ReservationSurvivesReloadRegardlessOfCastResult(bool accepted)
    {
        AutoTeleportState? saved = null;
        var policy = new AutoTeleportPolicy(persist: state => saved = Clone(state));
        Consume(policy);
        // The game callback happens only after Begin; durable state already contains its key.
        Assert.Contains(new TeleportAttempt(Town.AetheryteId, Town.SubIndex), saved!.Attempts);
        _ = accepted;
        var reloaded = new AutoTeleportPolicy(Clone(saved));
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(reloaded, 0, utc: Epoch + 5));
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(reloaded, 500_000, utc: Epoch + 500_005));
    }

    [Fact]
    public void NewDestinationAfterReloadRetainsRemainingGlobalCooldown()
    {
        var first = new AutoTeleportPolicy(); Consume(first);
        var next = new AutoTeleportPolicy(Clone(first.Snapshot()));
        Assert.Equal(AutoTeleportDecision.Settling, Step(next, 0, Other, utc: Epoch + 10));
        Assert.Equal(AutoTeleportDecision.CoolingDown, Step(next, 2, Other, utc: Epoch + 12));
        Assert.Equal(AutoTeleportDecision.Begin, Step(next, 22, Other, utc: Epoch + 32));
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(next, 23, utc: Epoch + 33));
    }

    [Fact]
    public void ExplicitRetryIsPersistedButDoesNotEraseAnotherConsumedOfferOrCooldown()
    {
        AutoTeleportState? saved = null;
        var policy = new AutoTeleportPolicy(persist: s => saved = Clone(s)); Consume(policy);
        Step(policy, 3, Other); Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 32, Other));
        Assert.True(policy.Retry(100, Town, 33, Epoch + 33));
        var reloaded = new AutoTeleportPolicy(saved);
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(reloaded, 0, Other, utc: Epoch + 34));
        Assert.Equal(AutoTeleportDecision.Settling, Step(reloaded, 1, utc: Epoch + 35));
        Assert.Equal(AutoTeleportDecision.CoolingDown, Step(reloaded, 3, utc: Epoch + 37));
        Assert.Equal(AutoTeleportDecision.Begin, Step(reloaded, 28, utc: Epoch + 62));
    }

    [Fact]
    public void LoadingZeroAndMissingGuidanceCannotForgetConsumedHistoryAcrossReload()
    {
        AutoTeleportState? saved = null;
        var policy = new AutoTeleportPolicy(persist: s => saved = Clone(s)); Consume(policy);
        Assert.Equal(AutoTeleportDecision.NoDestination, Step(policy, 4, origin: 0));
        Assert.Equal(AutoTeleportDecision.NoDestination, policy.Decide(true, 100, null, true, 2000, 5, Epoch + 5));
        Assert.Equal(AutoTeleportDecision.Off, Step(policy, 6, enabled: false));
        Assert.Equal(100u, saved!.OriginTerritory);
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(new(saved), 0, utc: Epoch + 40));
    }

    [Fact]
    public void RealZoneTransitionIsDurableEvenBeforeAnotherCastAndDoesNotBypassCooldown()
    {
        AutoTeleportState? saved = null;
        var policy = new AutoTeleportPolicy(persist: s => saved = Clone(s)); Consume(policy);
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 4, origin: 101));
        Assert.Equal(101u, saved!.OriginTerritory); Assert.Empty(saved.Attempts);
        var reloaded = new AutoTeleportPolicy(saved);
        Assert.Equal(AutoTeleportDecision.Settling, Step(reloaded, 0, origin: 101, utc: Epoch + 5));
        Assert.Equal(AutoTeleportDecision.CoolingDown, Step(reloaded, 2, origin: 101, utc: Epoch + 7));
        Assert.Equal(AutoTeleportDecision.Begin, Step(reloaded, 27, origin: 101, utc: Epoch + 32));
    }

    [Fact]
    public void ReturningToEarlierZoneIsANewVisitNotAnAccidentalReloadRearm()
    {
        var policy = new AutoTeleportPolicy(); Consume(policy);
        Step(policy, 3, origin: 101, enabled: false);
        var reloaded = new AutoTeleportPolicy(Clone(policy.Snapshot()));
        Assert.Equal(AutoTeleportDecision.Settling, Step(reloaded, 0, origin: 100, utc: Epoch + 40));
        Assert.Equal(AutoTeleportDecision.Begin, Step(reloaded, 2, origin: 100, utc: Epoch + 42));
    }

    [Fact]
    public void FutureSavedClockRestoresOnlyABoundedCooldownWithoutDroppingOffers()
    {
        var first = new AutoTeleportPolicy(); Consume(first);
        var future = first.Snapshot() with { LastAttemptUtcSeconds = Epoch + 10_000_000 };
        var policy = new AutoTeleportPolicy(future);
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 0, Other));
        Assert.Equal(AutoTeleportDecision.CoolingDown, Step(policy, 2, Other));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 30, Other));
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(policy, 31));
    }

    [Fact]
    public void AncientSavedClockExpiresOnlyCooldownNotSameZoneConsumedKeys()
    {
        var first = new AutoTeleportPolicy(); Consume(first);
        var policy = new AutoTeleportPolicy(first.Snapshot() with { LastAttemptUtcSeconds = 1 });
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(policy, 0));
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 1, Other));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 3, Other));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(1e300)]
    public void InvalidCurrentClockNeverCastsOrDeletesHistory(double time)
    {
        var policy = new AutoTeleportPolicy(); Consume(policy);
        Assert.Equal(AutoTeleportDecision.Unsafe, Step(policy, time, Other));
        Assert.Equal(AutoTeleportDecision.Unsafe, Step(policy, 10, Other, utc: time));
        Assert.Single(policy.Snapshot().Attempts);
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(policy, 12));
    }

    [Fact]
    public void MonotonicOrWallClockRollbackRestartsHoldAndBoundedCooldown()
    {
        var policy = new AutoTeleportPolicy(); Consume(policy);
        Step(policy, 10, Other);
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 1, Other, utc: Epoch + 11));
        Assert.Equal(AutoTeleportDecision.CoolingDown, Step(policy, 3, Other, utc: Epoch + 13));
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 4, Other, utc: Epoch + 5));
        Assert.Equal(AutoTeleportDecision.CoolingDown, Step(policy, 6, Other, utc: Epoch + 7));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 34, Other, utc: Epoch + 35));
    }

    [Fact]
    public void FailedReservationNeverReturnsBeginAndDoesNotAutomaticallyRetryStorage()
    {
        var writes = 0; var fail = true;
        AutoTeleportState? saved = null;
        var policy = new AutoTeleportPolicy(persist: state =>
        {
            writes++;
            if (fail && state.Attempts.Length > 0) throw new IOException("simulated full disk");
            saved = Clone(state);
        });
        Step(policy, 0);
        Assert.Equal(AutoTeleportDecision.HistoryUnavailable, Step(policy, 2));
        Assert.Empty(saved!.Attempts);
        var count = writes;
        Assert.Equal(AutoTeleportDecision.HistoryUnavailable, Step(policy, 100)); Assert.Equal(count, writes);
        fail = false;
        Assert.True(policy.Retry(100, Town, 101, Epoch + 101));
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 102));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 104));
        Assert.Single(saved.Attempts);
    }

    [Fact]
    public void FirstCheckpointFailureAlsoCannotStartAnUnrecordedCast()
    {
        var policy = new AutoTeleportPolicy(persist: _ => throw new IOException());
        Assert.Equal(AutoTeleportDecision.HistoryUnavailable, Step(policy, 0));
        Assert.Equal(AutoTeleportDecision.HistoryUnavailable, Step(policy, 1000));
        Assert.False(policy.Retry(100, Town, 1001, Epoch + 1001));
    }

    [Fact]
    public void InvalidJournalNeedsExplicitRetryAndFreshCooldown()
    {
        AutoTeleportState? saved = null;
        var policy = new AutoTeleportPolicy(new() { Version = 0 }, s => saved = Clone(s));
        Assert.Equal(AutoTeleportDecision.HistoryUnavailable, Step(policy, 0)); Assert.Null(saved);
        Assert.Equal(AutoTeleportDecision.HistoryUnavailable, Step(policy, 100));
        Assert.True(policy.Retry(100, Town, 101, Epoch + 101));
        Assert.Equal(1, saved!.Version);
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 102));
        Assert.Equal(AutoTeleportDecision.CoolingDown, Step(policy, 104));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 131));
    }

    [Fact]
    public void InvalidSavedDataNeverSilentlyBecomesFreshHistory()
    {
        AutoTeleportState[] invalid =
        [
            new() { Version = 99 }, new() { Attempts = null! },
            new() { OriginTerritory = 100, Attempts = [new(0, 0)], LastAttemptUtcSeconds = Epoch },
            new() { OriginTerritory = 100, Attempts = [new(42, 0), new(42, 0)], LastAttemptUtcSeconds = Epoch },
            new() { Attempts = [new(42, 0)], LastAttemptUtcSeconds = Epoch },
            new() { OriginTerritory = 100, Attempts = [new(42, 0)] },
            new() { LastAttemptUtcSeconds = double.NaN }, new() { LastAttemptUtcSeconds = -1 },
            new() { GuardedOffer = new(42, 0) },
            new() { OriginTerritory = 100, Attempts = Enumerable.Range(1, 257).Select(x => new TeleportAttempt((uint)x, 0)).ToArray(), LastAttemptUtcSeconds = Epoch },
        ];
        foreach (var state in invalid)
            Assert.Equal(AutoTeleportDecision.HistoryUnavailable, Step(new(state), 0));
    }

    [Fact]
    public void CapacityNeverEvictsAConsumedOfferToMakeAnotherAutomaticAttempt()
    {
        var state = new AutoTeleportState { OriginTerritory = 100, LastAttemptUtcSeconds = 1,
            Attempts = Enumerable.Range(1, AutoTeleportPolicy.MaximumAttempts).Select(x => new TeleportAttempt((uint)x, 0)).ToArray() };
        var policy = new AutoTeleportPolicy(state);
        Assert.Equal(AutoTeleportDecision.HistoryFull, Step(policy, 0, Town with { AetheryteId = 1000 }));
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(policy, 1));
        Assert.Equal(AutoTeleportPolicy.MaximumAttempts, policy.Snapshot().Attempts.Length);
    }

    [Fact]
    public void LegacyGuardIsExplicitDurableAndOnlyGuardsOneInitialOffer()
    {
        AutoTeleportState? saved = null;
        var policy = new AutoTeleportPolicy(new() { GuardNextOffer = true }, s => saved = Clone(s));
        Assert.Equal(AutoTeleportDecision.UpgradeGuard, Step(policy, 0));
        Assert.False(saved!.GuardNextOffer); Assert.Equal(new TeleportAttempt(42, 0), saved.GuardedOffer);
        policy = new(saved, s => saved = Clone(s));
        Assert.Equal(AutoTeleportDecision.UpgradeGuard, Step(policy, 0, utc: Epoch + 5));
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 1, Other, utc: Epoch + 6));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 25, Other, utc: Epoch + 30));
        Assert.True(policy.Retry(100, Town, 26, Epoch + 31));
        Assert.Null(saved!.GuardedOffer);
    }

    [Fact]
    public void FreshAutoOffInstallCanLaterOptInWithoutAMigrationGuard()
    {
        AutoTeleportState? saved = null;
        var policy = new AutoTeleportPolicy(new() { GuardNextOffer = false }, s => saved = Clone(s));
        Assert.Equal(AutoTeleportDecision.Off, Step(policy, 0, enabled: false));
        policy = new(saved);
        Consume(policy);
    }

    [Fact]
    public void EarlyExplicitLegacyRetryStillWaitsForUnknownPreviousCooldown()
    {
        var policy = new AutoTeleportPolicy(new() { GuardNextOffer = true });
        Assert.True(policy.Retry(100, Town, 0, Epoch));
        Assert.Equal(AutoTeleportDecision.Settling, Step(policy, 1));
        Assert.Equal(AutoTeleportDecision.CoolingDown, Step(policy, 3));
        Assert.Equal(AutoTeleportDecision.Begin, Step(policy, 30));
    }

    [Fact]
    public void ReturnedSnapshotsCannotMutateLiveAttemptLatches()
    {
        var policy = new AutoTeleportPolicy(); Consume(policy);
        var snapshot = policy.Snapshot(); snapshot.Attempts[0] = new(99, 0);
        Assert.Equal(AutoTeleportDecision.AlreadyAttempted, Step(policy, 40));
    }
}
