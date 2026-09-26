using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using XivWayfinder.Core;
using XivWayfinder.Shared;

namespace XivWayfinder.Plugin;

/// <summary>
/// XivWayfinder: a softly pulsing bead and a pointing glove that show which way to head, towards a target set by
/// command or IPC, the map flag, or the tracked quest's next step. Pointing only: it never moves the character.
/// </summary>
public sealed unsafe class Plugin : IDalamudPlugin
{
    public const string Command = "/wayfinder";

    /// <summary>The same command under the plugin's full name, left out of /xlhelp.</summary>
    public const string AliasCommand = "/xivwayfinder";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly IChatGui chat;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IKeyState keyState;
    private readonly WindowSystem windowSystem = new("XivWayfinder");
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ArrivalTimer arrival = new();
    private readonly Configuration config = null!;
    private readonly GameReader reader = null!;
    private readonly Navmesh navmesh = null!;
    private readonly GloveTexture glove = null!;
    private readonly GloveMinion minion = null!;
    private readonly Overlay overlay = null!;
    private readonly SettingsWindow window = null!;
    private readonly ICallGateProvider<uint, float, float, string, bool>? setTarget;
    private readonly ICallGateProvider<uint, float, float, string, bool>? setMapTarget;
    private readonly ICallGateProvider<bool>? clearTarget;
    private readonly ICallGateProvider<string>? getState;
    private delegate void OpenMapDelegate(AgentMap* agent, OpenMapInfo* info);

    private readonly Hook<OpenMapDelegate>? openMapHook;
    private readonly ArrivalTimer linkArrival = new();
    private bool commandRegistered;
    private bool aliasRegistered;

    // written by the framework update, read by Draw and by IPC callers on other threads
    private volatile Target? explicitTarget;
    private volatile WayfinderState state = new();
    private volatile uint territory;
    private Target? flag;
    private Target? quest;
    private Target? arrivalFor;

    // following a map link (MapLinks): the source to follow instead of config.Mode, and the quest it showed
    private volatile object? linkMode; // a boxed SourceMode, or null
    private volatile ushort linkQuest;
    private Target? lastFlagSeen;
    private double flagChangedAt = double.NegativeInfinity;
    private double lastScan = double.NegativeInfinity;
    private double lastUpdate = double.NaN;
    private PointerStyle lastStyle;
    private Scene scene;

    public Plugin(IDalamudPluginInterface pluginInterface, IPluginLog log, IFramework framework, ICommandManager commands, IChatGui chat,
        IClientState clientState, IObjectTable objects, ICondition condition, IGameGui gameGui, IDataManager data, ITextureProvider textures,
        IAetheryteList aetherytes, IGameInteropProvider interop, IKeyState keyState)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.framework = framework;
        this.commands = commands;
        this.chat = chat;
        this.clientState = clientState;
        this.objects = objects;
        this.condition = condition;
        this.gameGui = gameGui;
        this.keyState = keyState;

        try
        {
            config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            config.Clamp();
            reader = new GameReader(data, aetherytes, log);
            navmesh = new Navmesh(pluginInterface, log);
            glove = new GloveTexture(textures, data, log);
            minion = new GloveMinion(data, log);
            lastStyle = config.Style;
            overlay = new Overlay(gameGui, config, glove, minion, navmesh, log);
            window = new SettingsWindow(config, Save, () => state, () => glove.Status, () => minion.Status, () => navmesh.Status, () => Test(TestDistanceDefault), ClearFromUi);
            windowSystem.AddWindow(window);

            framework.Update += OnUpdate;
            pluginInterface.UiBuilder.Draw += DrawUi;
            pluginInterface.UiBuilder.OpenMainUi += OpenUi;
            pluginInterface.UiBuilder.OpenConfigUi += OpenUi;

            commandRegistered = commands.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Points the way: a glowing bead and a pointing glove. " + WayfinderCommands.Help,
            });
            aliasRegistered = commands.AddHandler(AliasCommand, new CommandInfo(OnCommand)
            {
                HelpMessage = "The same as " + Command + ".",
                ShowInHelp = false,
            });

            setTarget = pluginInterface.GetIpcProvider<uint, float, float, string, bool>(IpcContract.SetTarget);
            setTarget.RegisterFunc(SetTargetFromIpc);
            setMapTarget = pluginInterface.GetIpcProvider<uint, float, float, string, bool>(IpcContract.SetMapTarget);
            setMapTarget.RegisterFunc(SetMapTargetFromIpc);
            clearTarget = pluginInterface.GetIpcProvider<bool>(IpcContract.Clear);
            clearTarget.RegisterFunc(ClearExplicit);
            getState = pluginInterface.GetIpcProvider<string>(IpcContract.GetState);
            getState.RegisterFunc(() => WayfinderIpc.StateJson(state));

            try
            {
                openMapHook = interop.HookFromAddress<OpenMapDelegate>((nint)AgentMap.MemberFunctionPointers.OpenMap, OpenMapDetour);
                openMapHook.Enable();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "XivWayfinder: could not watch the map opening; map links open the map as usual");
            }
        }
        catch
        {
            // Dalamud does not call Dispose when the constructor throws; release what was acquired.
            DisposeCore();
            throw;
        }
    }

    private const float TestDistanceDefault = WayfinderCommands.TestDistance;

    /// <summary>How far along the route the minion glove looks to decide where to point, yalms.</summary>
    private const float MinionLookAhead = 4f;

    private double Now => clock.Elapsed.TotalSeconds;

    private void OnUpdate(IFramework _)
    {
        try
        {
            Update();
        }
        catch (Exception ex)
        {
            log.Error(ex, "XivWayfinder: update failed");
        }
    }

    private void Update()
    {
        var player = objects.LocalPlayer;
        territory = clientState.TerritoryType;
        var zone = territory;
        var situation = new Situation(
            HasPlayer: player is not null && clientState.IsLoggedIn,
            UiHidden: gameGui.GameUiHidden,
            Cutscene: condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78],
            BetweenAreas: condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] || condition[ConditionFlag.LoggingOut],
            GPose: clientState.IsGPosing,
            InCombat: condition[ConditionFlag.InCombat],
            InDuty: condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95]);
        var (visible, reason) = Visibility.Decide(situation, new VisibilityRules(config.Enabled, config.HideInCombat, config.HideInDuty));
        var now = Now;
        var dt = double.IsNaN(lastUpdate) ? 0f : (float)Math.Clamp(now - lastUpdate, 0, 0.25);
        lastUpdate = now;
        if (config.Style != lastStyle)
        {
            lastStyle = config.Style;
            minion.Retry();
        }

        if (player is null)
        {
            minion.Update(MinionWant.Hard, null, config.MinionSize, config.MinionTilt, now, dt);
            navmesh.Idle();
            scene = default;
            state = new WayfinderState { Enabled = config.Enabled, Visible = false, Reason = reason, Mode = config.Mode, Style = config.Style };
            return;
        }

        var position = player.Position;
        var mode = linkMode is SourceMode m ? m : config.Mode;
        if (now - lastScan >= 0.25)
        {
            lastScan = now;
            flag = config.UseFlag ? reader.Flag() : null;
            if (!SameFlag(flag, lastFlagSeen))
                flagChangedAt = now;
            lastFlagSeen = flag;
            quest = config.UseQuest && mode is SourceMode.Auto or SourceMode.Quest ? reader.Quest(zone, position, linkQuest) : null;
        }

        var target = TargetPicker.Pick(mode, config.Toggles(), explicitTarget, flag, quest);
        Vector3? aim = null;
        if (target is not null && target.TerritoryId == zone && config.UseNavmesh)
            aim = navmesh.Aim(zone, position, target.Position(position.Y), target.Y.HasValue, now);
        else
            navmesh.Idle();

        IReadOnlyList<AetheryteSpot> spots = target is not null && target.TerritoryId != zone && config.AetheryteHint
            ? reader.AetherytesIn(target.TerritoryId)
            : [];
        var guide = TargetPicker.Plan(zone, position, target, config.ArriveRadius, spots, aim);

        var mine = explicitTarget;
        if (!ReferenceEquals(mine, arrivalFor))
        {
            // a new target, from a command or IPC, starts its own arrival clock
            arrival.Reset();
            arrivalFor = mine;
        }

        if (mine is not null && ReferenceEquals(guide.Target, mine) && config.ClearOnArrival)
        {
            if (arrival.Update(guide.Kind == GuideKind.Arrived, now, 1.5))
            {
                explicitTarget = null;
                arrival.Reset();
                Print($"you have arrived{(mine.Label.Length > 0 ? " at " + mine.Label : "")}; target cleared.");
            }
        }
        else
        {
            arrival.Reset();
        }

        // a followed map link ends once you get there: back to the usual sources
        if (linkMode is not null && linkArrival.Update(guide.Kind == GuideKind.Arrived, now, 1.5))
        {
            EndLink();
            Print("you have arrived; following the usual way again.");
        }

        UpdateMinion(visible, guide, position, aim, now, dt);

        var targetZone = guide.Target is { } t ? reader.ZoneName(t.TerritoryId) : "";
        scene = new Scene(visible, guide, position, player.Rotation, aim, targetZone);
        state = new WayfinderState
        {
            Enabled = config.Enabled,
            Visible = visible && guide.Kind != GuideKind.None,
            Reason = guide.Kind == GuideKind.None && visible ? "nothing to point at" : reason,
            Mode = mode,
            Style = config.Style,
            PlayerTerritoryId = zone,
            Guide = guide,
            ZoneName = targetZone,
            Navmesh = aim is not null,
            RouteNote = aim is not null ? "" : config.UseNavmesh ? navmesh.Status : "switched off in the settings",
        };
    }

    /// <summary>The minion glove: where it floats and which way it points, or why it should not be there.</summary>
    private void UpdateMinion(bool visible, Guide guide, Vector3 player, Vector3? aim, double now, float dt)
    {
        MinionWant want;
        MinionPose? pose = null;
        if (!visible || config.Style != PointerStyle.Minion)
        {
            want = MinionWant.Hard;
        }
        else if (guide.Kind != GuideKind.Walk || guide.Target is not { } target)
        {
            want = MinionWant.Soft;
        }
        else
        {
            var layout = config.MinionPlacement();
            var dir = guide.Direction;
            var goal = aim ?? target.Position(player.Y);
            var heightKnown = aim is not null || target.Y.HasValue;
            if (navmesh.Following && navmesh.Path is { Length: >= 2 } path)
            {
                // along the route: towards a point a few yalms further along it, so the finger follows the bends
                // and tilts with the slope underfoot rather than aiming at the target or a far corner
                var ahead = PathTrail.LookAhead(path, PathTrail.Project(path, player, out _), MinionLookAhead);
                var along = Heading.DirectionTo(player, ahead);
                if (along != Vector2.Zero)
                {
                    dir = along;
                    goal = ahead;
                    heightKnown = true;
                }
            }

            Vector3? at;
            if (config.MinionLeads)
            {
                // leading: ahead along the route, pointing on along it from there
                var route = navmesh.Following && navmesh.Path is { Length: >= 2 } p ? p : [];
                var lead = MinionGlove.Lead(player, route, guide.Direction, config.MinionLeadDistance, layout, now);
                at = lead?.At;
                if (lead is { } l)
                {
                    dir = l.Dir;
                    if (route.Length >= 2)
                    {
                        goal = PathTrail.LookAhead(route, PathTrail.Project(route, player, out _), config.MinionLeadDistance + MinionLookAhead);
                        heightKnown = true;
                    }
                }
            }
            else
            {
                at = MinionGlove.Place(player, dir, layout, now);
            }

            want = at is null ? MinionWant.Soft : MinionWant.Show;
            if (at is { } a)
            {
                var pitch = MinionGlove.Pitch(a, goal, heightKnown, layout.MaxPitchDegrees);
                pose = new MinionPose(a, MinionGlove.Yaw(dir, layout.TurnDegrees), pitch);
            }
        }

        minion.Update(want, pose, config.MinionSize, config.MinionTilt, now, dt);
    }

    private void DrawUi()
    {
        try
        {
            windowSystem.Draw();
        }
        catch (Exception ex)
        {
            log.Error(ex, "XivWayfinder: settings window failed");
        }

        var frame = scene;
        if (frame.Guide is not null)
        {
            // the character moves between the update and this draw; point from where it is now
            if (frame.Visible && objects.LocalPlayer is { } player)
                frame = frame with { Player = player.Position, Rotation = player.Rotation };
            overlay.Draw(frame);
        }
    }

    private void OpenUi() => window.IsOpen = true;

    private void OnCommand(string command, string arguments)
    {
        try
        {
            var request = WayfinderCommands.Parse(arguments);
            switch (request.Verb)
            {
                case Verb.Open:
                    window.Toggle();
                    break;
                case Verb.Help:
                    Print(WayfinderCommands.Help);
                    break;
                case Verb.Invalid:
                    Print(request.Error);
                    break;
                case Verb.Clear:
                    Print(ClearExplicit() ? "target cleared." : "there was no /wayfinder target; the flag and quests are still followed.");
                    break;
                case Verb.Test:
                    Test(request.Distance);
                    break;
                case Verb.MapTarget:
                    SetMapTargetFromCommand(request);
                    break;
                case Verb.Mode:
                    EndLink();
                    config.Mode = request.Mode;
                    Save();
                    Print("following " + WayfinderIpc.Name(request.Mode) + (request.Mode == SourceMode.Auto ? ": target, then flag, then quest." : " only."));
                    break;
                case Verb.Style:
                    config.Style = request.Style;
                    Save();
                    Print(request.Style == PointerStyle.Minion
                        ? "pointer: the Wind-up Cursor minion glove, shown only to you (" + minion.Status + ")."
                        : "pointer: " + request.Style.ToString().ToLowerInvariant() + ".");
                    break;
                case Verb.On or Verb.Off or Verb.Toggle:
                    config.Enabled = request.Verb == Verb.On || (request.Verb == Verb.Toggle && !config.Enabled);
                    Save();
                    Print(config.Enabled ? "pointer on." : "pointer off.");
                    break;
                case Verb.Status:
                    Print(WayfinderIpc.StatusLine(state));
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "{Command} failed", Command);
        }
    }

    /// <summary>/wayfinder test: a target straight ahead of the character, so the pointer can be seen working.</summary>
    private void Test(float distance)
    {
        if (objects.LocalPlayer is not { } player || clientState.TerritoryType == 0)
        {
            Print("no character to measure from.");
            return;
        }

        var ahead = Heading.Ahead(player.Position, Heading.Forward(player.Rotation), distance, 0f);
        var label = string.Create(CultureInfo.InvariantCulture, $"test: {distance:0} yalms ahead");
        UseExplicit(new Target(TargetSource.Explicit, clientState.TerritoryType, ahead.X, ahead.Z, player.Position.Y, label));
        Print(label + ". Walk towards it, turn away and back: the bead brightens when you face away and dims when you face it. /wayfinder clear removes it.");
    }

    private void SetMapTargetFromCommand(WayfinderRequest request)
    {
        uint zone = clientState.TerritoryType;
        if (request.Zone.Length > 0)
        {
            if (ZoneMatch.Find(request.Zone, reader.Zones()) is not { } found)
            {
                Print($"no zone called \"{request.Zone}\".");
                return;
            }

            zone = found.TerritoryId;
        }

        if (zone == 0)
        {
            Print("not in a zone; name one: /wayfinder X Y zone.");
            return;
        }

        var map = zone == clientState.TerritoryType ? reader.Map(zone, clientState.MapId) : reader.Map(zone);
        if (map is not { } m)
        {
            Print($"{reader.ZoneName(zone)} has no map to read coordinates on.");
            return;
        }

        var x = MapCoords.MapToWorld(request.X, m.SizeFactor, m.OffsetX);
        var z = MapCoords.MapToWorld(request.Y, m.SizeFactor, m.OffsetY);
        var label = string.Create(CultureInfo.InvariantCulture, $"{reader.ZoneName(zone)} ({request.X:0.0}, {request.Y:0.0})");
        UseExplicit(new Target(TargetSource.Explicit, zone, x, z, null, label));
        Print("pointing at " + label + ".");
    }

    /// <summary>A target the player asked for by command: make sure the settings let it show.</summary>
    private void UseExplicit(Target target)
    {
        explicitTarget = target;
        var notes = new List<string>();
        if (!config.UseExplicit)
        {
            config.UseExplicit = true;
            notes.Add("targets from /wayfinder switched back on");
        }

        if (config.Mode is SourceMode.Flag or SourceMode.Quest)
        {
            config.Mode = SourceMode.Auto;
            notes.Add("following everything by priority again");
        }

        if (!config.Enabled)
        {
            config.Enabled = true;
            notes.Add("pointer switched on");
        }

        if (notes.Count > 0)
        {
            Save();
            Print(string.Join("; ", notes) + ".");
        }
    }

    private bool SetTargetFromIpc(uint territoryId, float x, float z, string label)
    {
        var target = WayfinderIpc.Validate(territoryId, x, z, label, territory, out var error);
        if (target is null)
        {
            log.Debug("XivWayfinder.v1.SetTarget refused: {Error}", error);
            return false;
        }

        explicitTarget = target;
        return true;
    }

    private bool SetMapTargetFromIpc(uint territoryId, float mapX, float mapY, string label)
    {
        var zone = territoryId == 0 ? territory : territoryId;
        if (zone == 0 || !MapCoords.IsPlausibleMapCoord(mapX) || !MapCoords.IsPlausibleMapCoord(mapY) || reader.Map(zone) is not { } m)
        {
            log.Debug("XivWayfinder.v1.SetMapTarget refused for territory {Territory}", zone);
            return false;
        }

        return SetTargetFromIpc(zone, MapCoords.MapToWorld(mapX, m.SizeFactor, m.OffsetX), MapCoords.MapToWorld(mapY, m.SizeFactor, m.OffsetY), label);
    }

    private bool ClearExplicit()
    {
        var had = explicitTarget is not null || linkMode is not null;
        explicitTarget = null;
        EndLink();
        return had;
    }

    // Map links ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The game opening its map (<c>AgentMap.OpenMap</c>, on the game thread): a &lt;flag&gt; link or a quest's Show
    /// on Map is followed instead (<see cref="MapLinks.Decide"/>); everything else opens as usual.
    /// </summary>
    private void OpenMapDetour(AgentMap* agent, OpenMapInfo* info)
    {
        try
        {
            if (info != null)
            {
                // a link click sets the flag and opens the map in the same frame, before the next scan sees it
                var now = Now;
                var current = config.UseFlag ? reader.Flag() : null;
                var since = current is null ? -1 : !SameFlag(current, lastFlagSeen) ? 0 : now - flagChangedAt;
                var action = MapLinks.Decide((uint)info->Type, info->QuestId, config.MapLinks && config.Enabled, keyState[VirtualKey.SHIFT], since);
                if (action != MapLinkAction.Open)
                {
                    FollowLink(action, MapLinks.JournalId(info->QuestId));
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "XivWayfinder: following a map link failed; opening the map");
        }

        openMapHook!.Original(agent, info);
    }

    private void FollowLink(MapLinkAction action, ushort questId)
    {
        linkMode = action == MapLinkAction.FollowQuest ? SourceMode.Quest : SourceMode.Flag;
        linkQuest = action == MapLinkAction.FollowQuest ? questId : (ushort)0;
        linkArrival.Reset();
        lastScan = double.NegativeInfinity; // look for it now
        var what = action == MapLinkAction.FollowQuest ? "that quest's next step" : "the flag";
        if (config.MapLinksMinion && config.Style != PointerStyle.Minion)
        {
            config.Style = PointerStyle.Minion;
            Save();
            what += " (the glove leads the way)";
        }

        Print($"leading you to {what} instead of opening the map; hold Shift to open the map.");
    }

    private void EndLink()
    {
        linkMode = null;
        linkQuest = 0;
        linkArrival.Reset();
    }

    private static bool SameFlag(Target? a, Target? b) =>
        a is null ? b is null : b is not null && a.TerritoryId == b.TerritoryId && a.X == b.X && a.Z == b.Z;

    private void ClearFromUi() => Print(ClearExplicit() ? "target cleared." : "there was no /wayfinder target to clear.");

    private void Save() => pluginInterface.SavePluginConfig(config);

    private void Print(string message)
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                chat.Print(message, "XivWayfinder");
            }
            catch
            {
                // Chat unavailable (e.g. title screen).
            }
        });
    }

    public void Dispose() => DisposeCore();

    /// <summary>Deletes the minion glove's client-side object, on the framework thread, before the plugin goes.</summary>
    private void RemoveMinion()
    {
        if (minion is null || framework.IsFrameworkUnloading)
            return;
        try
        {
            if (framework.IsInFrameworkUpdateThread)
                minion.Remove();
            else if (!framework.RunOnFrameworkThread(minion.Remove).Wait(TimeSpan.FromSeconds(2)))
                log.Warning("XivWayfinder: removing the minion glove timed out");
        }
        catch (Exception ex)
        {
            log.Error(ex, "XivWayfinder: removing the minion glove failed");
        }
    }

    private void DisposeCore()
    {
        if (commandRegistered)
        {
            commands.RemoveHandler(Command);
            commandRegistered = false;
        }

        if (aliasRegistered)
        {
            commands.RemoveHandler(AliasCommand);
            aliasRegistered = false;
        }

        setTarget?.UnregisterFunc();
        setMapTarget?.UnregisterFunc();
        clearTarget?.UnregisterFunc();
        getState?.UnregisterFunc();
        framework.Update -= OnUpdate;
        openMapHook?.Dispose();
        RemoveMinion();
        pluginInterface.UiBuilder.Draw -= DrawUi;
        pluginInterface.UiBuilder.OpenMainUi -= OpenUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenUi;
        windowSystem.RemoveAllWindows();
        navmesh?.Dispose();
    }
}
