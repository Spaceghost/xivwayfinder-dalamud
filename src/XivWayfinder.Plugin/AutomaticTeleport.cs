using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

/// <summary>Framework-thread Teleport execution. No path movement, input injection or automatic retries.</summary>
internal sealed unsafe class AutomaticTeleport(
    IAetheryteList aetherytes, ICondition condition, IClientState clientState, IObjectTable objects,
    IGameGui gameGui, IPluginLog log, Action<string> print, string stateDirectory, IPlayerState playerState)
{
    private AutoTeleportPolicy? policy;
    private ulong ownerContentId;
    private Vector3? stationaryAt;
    private string lastResult = "";
    private (uint Id, byte SubIndex)? lastDestination;
    private int retryRequested;
    public string Status { get; private set; } = "Automatic teleport is off.";

    private static readonly (ConditionFlag Flag, string Reason)[] Busy =
    [
        (ConditionFlag.InCombat, "in combat"),
        (ConditionFlag.Casting, "casting"),
        (ConditionFlag.Casting87, "casting"),
        (ConditionFlag.BetweenAreas, "changing zones"),
        (ConditionFlag.BetweenAreas51, "changing zones"),
        (ConditionFlag.WatchingCutscene, "watching a cutscene"),
        (ConditionFlag.WatchingCutscene78, "watching a cutscene"),
        (ConditionFlag.OccupiedInCutSceneEvent, "in a cutscene"),
        (ConditionFlag.OccupiedInEvent, "talking to an NPC or in an event"),
        (ConditionFlag.OccupiedInQuestEvent, "in a quest event"),
        (ConditionFlag.Occupied, "occupied"),
        (ConditionFlag.Occupied30, "occupied"),
        (ConditionFlag.Occupied33, "occupied"),
        (ConditionFlag.Occupied38, "occupied"),
        (ConditionFlag.Occupied39, "occupied"),
        (ConditionFlag.OccupiedSummoningBell, "using the summoning bell"),
        (ConditionFlag.TradeOpen, "trading"),
        (ConditionFlag.PreparingToCraft, "preparing to craft"),
        (ConditionFlag.Crafting, "crafting"),
        (ConditionFlag.ExecutingCraftingAction, "crafting"),
        (ConditionFlag.Gathering, "gathering"),
        (ConditionFlag.ExecutingGatheringAction, "gathering"),
        (ConditionFlag.Fishing, "fishing"),
        (ConditionFlag.Unconscious, "knocked out"),
        (ConditionFlag.LoggingOut, "logging out"),
        (ConditionFlag.BeingMoved, "being moved"),
        (ConditionFlag.Jumping, "jumping"),
        (ConditionFlag.Jumping61, "jumping"),
        (ConditionFlag.Performing, "performing music"),
        (ConditionFlag.InFlight, "flying"),
        (ConditionFlag.Diving, "diving"),
        (ConditionFlag.BoundByDuty, "in a duty"),
        (ConditionFlag.BoundByDuty56, "in a duty"),
        (ConditionFlag.BoundByDuty95, "in a duty"),
        (ConditionFlag.InDuelingArea, "in a dueling area"),
        (ConditionFlag.UsingChocoboTaxi, "riding a chocobo porter"),
        (ConditionFlag.MountOrOrnamentTransition, "mounting or changing accessories"),
    ];

    public void Update(Configuration config, Guide guide, bool visible, double now)
    {
        var enabled = config.Enabled && config.AutoTeleport;
        var contentId = playerState.IsLoaded ? playerState.ContentId : 0;
        if (!clientState.IsLoggedIn || contentId == 0)
        {
            policy = null; ownerContentId = 0; stationaryAt = null;
            Interlocked.Exchange(ref retryRequested, 0);
            Status = enabled ? "Waiting for a logged-in character before opening teleport history." : "Automatic teleport is off.";
            return;
        }
        if (ownerContentId != contentId)
        {
            policy = null; ownerContentId = contentId; stationaryAt = null;
            lastDestination = null; lastResult = "";
            Interlocked.Exchange(ref retryRequested, 0);
        }
        // Initialization and every bounded journal write happen on this same
        // framework thread, never from a UI/IPC worker or native draw callback.
        policy ??= CreatePolicy(config.AutoTeleport, contentId);
        var utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        var blocked = BlockedReason(visible);
        TeleportOffer? offer = null;
        var name = "";
        // Revalidate the actual attuned entry each frame. A map hint alone never authorizes a cast.
        if (enabled && guide.Kind == GuideKind.Teleport && guide.Target is { } target &&
            guide.Aetheryte is { Unlocked: true, Id: > 0 } spot && spot.TerritoryId == target.TerritoryId)
        {
            foreach (var entry in aetherytes)
            {
                if (entry is null || entry.AetheryteId != spot.Id || entry.TerritoryId != target.TerritoryId ||
                    entry.Ward != 0 || entry.Plot != 0 || entry.IsApartment || entry.IsSharedHouse)
                    continue; // A zone-level guide is not an estate/ward selection.
                offer = new TeleportOffer(entry.AetheryteId, entry.SubIndex, entry.TerritoryId, entry.GilCost);
                name = WayfinderIpc.CleanLabel(spot.Name);
                break;
            }
        }

        if (Interlocked.Exchange(ref retryRequested, 0) != 0)
            policy.Retry(clientState.TerritoryType, offer, now, utc);
        var decision = policy.Decide(enabled, clientState.TerritoryType, offer, blocked is null, config.AutoTeleportMaxGil, now, utc);
        Status = decision switch
        {
            AutoTeleportDecision.Off => "Automatic teleport is off.",
            AutoTeleportDecision.NoDestination => "Waiting for an attuned teleport destination in the guidance.",
            AutoTeleportDecision.Unsafe => "Waiting: " + (blocked ?? "game is not ready") + ".",
            AutoTeleportDecision.OverBudget => $"{name} costs {offer!.Value.GilCost:N0} gil; above the {config.AutoTeleportMaxGil:N0} gil limit.",
            AutoTeleportDecision.AlreadyAttempted =>
                (offer is { } attempted && lastDestination == (attempted.AetheryteId, attempted.SubIndex)
                    ? lastResult : "This destination has already been attempted from this zone.")
                + " Use Retry automatic teleport after an interrupted/refused cast.",
            AutoTeleportDecision.Settling => $"Preparing Teleport to {name} ({offer!.Value.GilCost:N0} gil); stay still for 2 seconds.",
            AutoTeleportDecision.CoolingDown => "Waiting for the 30-second teleport cooldown.",
            AutoTeleportDecision.UpgradeGuard => "Teleport safety was upgraded with no prior saved history. This first offer needs Retry automatic teleport; it has not been cast by the upgrade.",
            AutoTeleportDecision.HistoryUnavailable => "Automatic teleport history could not be read or saved; no cast was attempted. Repair storage then use Retry; reload if history could not be read or another instance changed it.",
            AutoTeleportDecision.HistoryFull => "This zone's saved teleport attempt history is full. Change zones, or explicitly Retry a previously attempted destination; existing attempts will not be forgotten automatically.",
            _ => "Starting Teleport.",
        };
        if (decision != AutoTeleportDecision.Begin || offer is not { } destination)
            return;
        lastDestination = (destination.AetheryteId, destination.SubIndex);

        try
        {
            var telepo = Telepo.Instance();
            var started = telepo != null && telepo->Teleport(destination.AetheryteId, destination.SubIndex);
            lastResult = started
                ? $"Teleport to {name} started ({destination.GilCost:N0} gil)."
                : $"The game refused Teleport to {name}. Check gil and availability.";
        }
        catch (Exception ex)
        {
            // The consumed policy attempt intentionally survives exceptions and cancellation.
            lastResult = $"Could not start Teleport to {name}; automatic retries are paused.";
            log.Warning(ex, "XivWayfinder automatic teleport failed");
        }
        Status = lastResult;
        print(lastResult);
    }

    public void Retry() => Interlocked.Exchange(ref retryRequested, 1);

    private AutoTeleportPolicy CreatePolicy(bool alreadyEnabled, ulong contentId)
    {
        var journal = new AutoTeleportJournal(Path.Combine(stateDirectory, AutoTeleportJournal.CharacterFileName(contentId)));
        AutoTeleportState? state;
        try { state = journal.Read(); }
        catch (Exception error)
        {
            state = new() { Version = 0 };
            log.Warning(error, "XivWayfinder could not read automatic teleport history; casting is paused");
        }
        // Preserve opt-in. Only legacy already-enabled installations need the
        // one-time guard, since their previous in-memory attempts are unknowable.
        state ??= new() { GuardNextOffer = alreadyEnabled };
        return new(state, snapshot =>
        {
            try { journal.Write(snapshot); }
            catch (Exception error)
            {
                log.Warning(error, "XivWayfinder could not durably reserve automatic teleport; no cast will be made");
                throw;
            }
        });
    }

    private string? BlockedReason(bool visible)
    {
        var player = objects.LocalPlayer;
        if (player is null || !clientState.IsLoggedIn)
        {
            stationaryAt = null;
            return "not logged in";
        }

        // Compare with the last stationary anchor, not frame-to-frame movement: even slow walking resets the hold.
        var position = player.Position;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
        {
            stationaryAt = null;
            return "position unavailable";
        }
        var moved = stationaryAt is not { } anchor || Vector3.DistanceSquared(anchor, position) > 0.0025f;
        if (moved) stationaryAt = position;
        if (!visible || gameGui.GameUiHidden) return "guidance is hidden";
        if (clientState.IsGPosing) return "in group pose";
        if (clientState.IsPvP) return "in PvP";
        if (player.CurrentHp == 0) return "knocked out";
        if (player.IsCasting) return "casting";
        foreach (var (flag, reason) in Busy)
            if (condition[flag]) return reason;
        return moved ? "moving" : null;
    }
}
