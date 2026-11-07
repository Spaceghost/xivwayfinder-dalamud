using System.Text.Json;

namespace XivWayfinder.Core;

/// <summary>A fresh quote from the character's attuned teleport list, not just a map marker.</summary>
public readonly record struct TeleportOffer(uint AetheryteId, byte SubIndex, uint TerritoryId, uint GilCost);

public enum AutoTeleportDecision
{
    Off,
    NoDestination,
    Unsafe,
    OverBudget,
    AlreadyAttempted,
    Settling,
    CoolingDown,
    UpgradeGuard,
    HistoryUnavailable,
    HistoryFull,
    Begin,
}

public readonly record struct TeleportAttempt(uint AetheryteId, byte SubIndex);

/// <summary>Bounded, character-agnostic safety journal. No names, positions, credentials or result text.</summary>
public sealed record AutoTeleportState
{
    public int Version { get; init; } = 1;
    public uint OriginTerritory { get; init; }
    public TeleportAttempt[] Attempts { get; init; } = [];
    public double? LastAttemptUtcSeconds { get; init; }
    public bool GuardNextOffer { get; init; }
    public TeleportAttempt? GuardedOffer { get; init; }

    public const int MaximumJsonBytes = 64 * 1024;

    /// <summary>Malformed/incomplete files retain a recovery barrier, never silently become empty history.</summary>
    public static AutoTeleportState Decode(string json)
    {
        if (json.Length > MaximumJsonBytes) return new() { Version = 0 };
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new() { Version = 0 };
            foreach (var property in new[] { "Version", "OriginTerritory", "Attempts", "LastAttemptUtcSeconds", "GuardNextOffer", "GuardedOffer" })
                if (!document.RootElement.TryGetProperty(property, out _)) return new() { Version = 0 };
            return JsonSerializer.Deserialize<AutoTeleportState>(json, new JsonSerializerOptions { MaxDepth = 8 })
                ?? new() { Version = 0 };
        }
        catch (JsonException) { return new() { Version = 0 }; }
    }
}

/// <summary>
/// Gates automatic Teleport casts. An attempt is consumed before calling the game, including refused or
/// interrupted casts. The optional persistence callback must complete before Begin can escape; the runtime
/// always supplies an atomic journal writer. Temporary missing guidance, source changes, reloads and toggles
/// cannot re-arm an offer. A new nonzero territory or an explicit Retry can.
/// </summary>
public sealed class AutoTeleportPolicy
{
    public const double SettleSeconds = 2;
    public const double CooldownSeconds = 30;
    public const int MaximumAttempts = 256;
    private const double MaximumClockSeconds = 253402300799; // DateTimeOffset.MaxValue, not an unbounded float
    private readonly HashSet<TeleportAttempt> attempted = [];
    private readonly Action<AutoTeleportState>? persist;
    private uint territory;
    private TeleportOffer? candidate;
    private TeleportOffer? stableOffer;
    private double stableSince = double.NaN;
    private double cooldownUntil = double.NegativeInfinity;
    private double previousNow = double.NaN, previousUtc = double.NaN;
    private double? lastAttemptUtc;
    private bool restoreCooldown, checkpointPending = true, recoveryRequired, persistenceFault, guardNextOffer;
    private TeleportAttempt? guardedOffer;

    public AutoTeleportPolicy(AutoTeleportState? restored = null, Action<AutoTeleportState>? persist = null)
    {
        this.persist = persist;
        if (restored is null) return;
        if (restored.Version != 1 || restored.Attempts is null || restored.Attempts.Length > MaximumAttempts
            || (restored.Attempts.Length > 0 && (restored.OriginTerritory == 0 || restored.LastAttemptUtcSeconds is null))
            || (restored.LastAttemptUtcSeconds is { } savedTime && !ValidClock(savedTime))
            || restored.Attempts.Any(x => x.AetheryteId == 0)
            || restored.Attempts.Distinct().Count() != restored.Attempts.Length
            || (restored.GuardedOffer is { } guard && !restored.Attempts.Contains(guard)))
        { recoveryRequired = true; return; }
        territory = restored.OriginTerritory;
        attempted.UnionWith(restored.Attempts);
        lastAttemptUtc = restored.LastAttemptUtcSeconds;
        restoreCooldown = lastAttemptUtc is not null;
        guardNextOffer = restored.GuardNextOffer;
        guardedOffer = restored.GuardedOffer;
    }

    public AutoTeleportState Snapshot() => new()
    {
        Version = recoveryRequired ? 0 : 1,
        OriginTerritory = territory,
        Attempts = attempted.OrderBy(x => x.AetheryteId).ThenBy(x => x.SubIndex).ToArray(),
        LastAttemptUtcSeconds = lastAttemptUtc,
        GuardNextOffer = guardNextOffer,
        GuardedOffer = guardedOffer,
    };

    public AutoTeleportDecision Decide(bool enabled, uint originTerritory, TeleportOffer? offer,
        bool safe, uint maxGil, double now, double? utcNowSeconds = null)
    {
        candidate = offer;
        var utc = utcNowSeconds ?? now;
        if (!ObserveClock(now, utc)) return Stop(enabled ? AutoTeleportDecision.Unsafe : AutoTeleportDecision.Off);
        if (recoveryRequired || persistenceFault)
            return Stop(enabled ? AutoTeleportDecision.HistoryUnavailable : AutoTeleportDecision.Off);
        // Zero is common while zoning. It must not clear the attempt latch and retry a cancelled cast.
        ObserveTerritory(originTerritory);
        if (checkpointPending && !Checkpoint())
            return Stop(enabled ? AutoTeleportDecision.HistoryUnavailable : AutoTeleportDecision.Off);

        AutoTeleportDecision? stop = !enabled ? AutoTeleportDecision.Off
            : !ValidOffer(originTerritory, offer) ? AutoTeleportDecision.NoDestination
            : null;
        if (stop is { } reason) return Stop(reason);

        var next = offer!.Value;
        var key = new TeleportAttempt(next.AetheryteId, next.SubIndex);
        if (attempted.Contains(key))
            return Stop(guardedOffer == key ? AutoTeleportDecision.UpgradeGuard : AutoTeleportDecision.AlreadyAttempted);
        if (attempted.Count == MaximumAttempts) return Stop(AutoTeleportDecision.HistoryFull);
        if (guardNextOffer)
        {
            // The old plugin never recorded attempts. Do not pretend to know
            // whether this offer was cast; explicitly require one acknowledgement.
            guardNextOffer = false;
            guardedOffer = key;
            Consume(key, now, utc);
            return Stop(Checkpoint() ? AutoTeleportDecision.UpgradeGuard : AutoTeleportDecision.HistoryUnavailable);
        }
        if (next.GilCost > maxGil) return Stop(AutoTeleportDecision.OverBudget);
        if (!safe) return Stop(AutoTeleportDecision.Unsafe);

        if (stableOffer != offer || double.IsNaN(stableSince) || now < stableSince)
        {
            stableOffer = offer;
            stableSince = now;
        }
        if (now - stableSince < SettleSeconds) return AutoTeleportDecision.Settling;
        if (now < cooldownUntil) return AutoTeleportDecision.CoolingDown;

        Consume(key, now, utc);
        return Checkpoint() ? AutoTeleportDecision.Begin : AutoTeleportDecision.HistoryUnavailable;
    }

    /// <summary>Retry only the latest observed offer, without bypassing any guard or cooldown.</summary>
    public bool Retry() => Retry(territory, candidate, previousNow, previousUtc);

    /// <summary>The runtime supplies the freshly revalidated offer, not an earlier UI frame's destination.</summary>
    public bool Retry(uint originTerritory, TeleportOffer? offer, double now, double utcNowSeconds)
    {
        if (!ValidOffer(originTerritory, offer) || !ObserveClock(now, utcNowSeconds)) return false;
        candidate = offer;
        var recovered = recoveryRequired || persistenceFault;
        if (recoveryRequired)
        {
            // Explicit acknowledgement is the only way to replace unreadable
            // history. Keep a fresh cooldown instead of treating it as never used.
            recoveryRequired = false;
            attempted.Clear(); guardedOffer = null; territory = originTerritory;
            lastAttemptUtc = utcNowSeconds;
            cooldownUntil = now + CooldownSeconds;
        }
        persistenceFault = false;
        ObserveTerritory(originTerritory);
        var key = new TeleportAttempt(offer!.Value.AetheryteId, offer.Value.SubIndex);
        var removed = attempted.Remove(key);
        var acknowledged = guardNextOffer;
        guardNextOffer = false;
        if (acknowledged)
        {
            // Even an explicit Retry arriving before the first legacy offer
            // is observed cannot assume the unknown old cooldown has expired.
            lastAttemptUtc = utcNowSeconds;
            cooldownUntil = Math.Max(cooldownUntil, now + CooldownSeconds);
        }
        if (guardedOffer == key) guardedOffer = null;
        ResetStability();
        if (!removed && !recovered && !acknowledged) return false;
        return Checkpoint();
    }

    private void Consume(TeleportAttempt key, double now, double utc)
    {
        attempted.Add(key);
        lastAttemptUtc = utc;
        cooldownUntil = now + CooldownSeconds;
        ResetStability();
    }

    private void ObserveTerritory(uint originTerritory)
    {
        if (originTerritory != 0 && territory != originTerritory)
        {
            territory = originTerritory;
            attempted.Clear(); guardedOffer = null;
            checkpointPending = true;
            ResetStability();
        }
    }

    private bool ObserveClock(double now, double utc)
    {
        if (!ValidClock(now) || !ValidClock(utc)) { ResetStability(); return false; }
        if (restoreCooldown)
        {
            // Never wait indefinitely for a future wall-clock timestamp. A
            // rollback restores a full bounded cooldown; it never clears offers.
            cooldownUntil = now + Math.Clamp(CooldownSeconds - (utc - lastAttemptUtc!.Value), 0, CooldownSeconds);
            restoreCooldown = false;
        }
        if (now < previousNow || utc < previousUtc)
        {
            cooldownUntil = now + CooldownSeconds;
            ResetStability();
        }
        previousNow = now; previousUtc = utc;
        return true;
    }

    private bool Checkpoint()
    {
        try { persist?.Invoke(Snapshot()); checkpointPending = false; return true; }
        catch (Exception)
        {
            // Do not return Begin when durable reservation failed. No automatic
            // write/cast retry loop; an explicit Retry may repair transient I/O.
            persistenceFault = true;
            ResetStability();
            return false;
        }
    }

    private AutoTeleportDecision Stop(AutoTeleportDecision decision)
    {
        ResetStability();
        return decision;
    }

    private static bool ValidOffer(uint origin, TeleportOffer? offer) => origin != 0
        && offer is { AetheryteId: > 0, TerritoryId: > 0 } next && next.TerritoryId != origin;

    private static bool ValidClock(double time) => double.IsFinite(time) && time >= 0 && time <= MaximumClockSeconds;

    private void ResetStability()
    {
        stableOffer = null;
        stableSince = double.NaN;
    }
}
