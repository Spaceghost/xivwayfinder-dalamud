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
/// command or IPC, the map flag, or the tracked quest's next step. Never walks the character; optional automatic Teleport.
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
    private readonly ArrivalTimer eventArrival = new();
    private readonly Configuration config = null!;
    private readonly GameReader reader = null!;
    private readonly Navmesh navmesh = null!;
    private readonly GuideRouteConnector companionConnector = null!;
    private readonly GuideApproachRouter companionApproach = null!;
    private readonly GuideApproachRouter idleApproach = null!;
    private readonly CompanionTeleportMotion idleRouteMotion = null!;
    private readonly CompanionIdleRendezvous idleRendezvous = new();
    private Vector3[]? idleMainRoute;
    private Target? idleTarget;
    private long idleContext;
    private long loggedIdleRequest, loggedIdleRoute;
    private bool loggedIdleClear;
    private bool companionWaitingAside;
    private readonly ZoneConnections connections = null!;
    private readonly AutomaticTeleport automaticTeleport = null!;
    private readonly GloveTexture glove = null!;
    private readonly GloveMinion minion = null!;
    private readonly PointerJourney pointerJourney = new();
    private PointerJourneyFrame pointerFrame;
    private readonly PointerAudio pointerAudio = null!;
    private readonly GuideWorldDepth worldDepth = null!;
    private readonly ImportedGuide imported = null!;
    private readonly GuideTeleportBoarding teleportBoarding = null!;
    private readonly ImportedGuide importedPreview = null!;
    private readonly PreviewPlacement nativePreviewPlacement = new(), localPreviewPlacement = new();
    private float nativePreviewDrawScale = 1;
    private readonly LocalModelLibrary localModels = null!;
    private readonly NativeFairyTrail fairyTrail = null!;
    private GuideChasePose? companionRoutePose;
    private MinionPose? companionAcceptedPose;
    private readonly CompanionIntent companionIntent = new();
    private readonly CompanionRouteMotion companionMotion = new();
    private readonly CompanionTeleportIntent companionTeleport = new();
    private readonly CompanionTeleportMotion companionTeleportMotion = null!;
    private readonly CompanionGestureCue companionGesture = new();
    private readonly GuideJumpProbe companionJumpProbe = new();
    private readonly CompanionJumpTraversal companionJump = null!;
    private readonly record struct CompanionVisualIdentity(string Path, uint Model, uint Npc, float Scale,
        bool Grounded, bool Animate, string Clip, bool InPlace, bool Preview, uint BNpc);
    private CompanionVisualIdentity? companionVisualIdentity;
    private uint companionTerritory;
    private readonly ModelLibrary modelLibrary = null!;
    private readonly VisualAssetLibrary visualAssets = null!;
    private readonly TrailGround guideGround = new();
    private readonly Overlay overlay = null!;
    private readonly TrailTerrainCheck terrainCheck = null!;
    private readonly SettingsWindow window = null!;
    private readonly ICallGateProvider<uint, float, float, string, bool>? setTarget;
    private readonly ICallGateProvider<uint, float, float, string, bool>? setMapTarget;
    private readonly ICallGateProvider<bool>? clearTarget;
    private readonly ICallGateProvider<string>? getState;
    private readonly ICallGateProvider<string>? getRoute;
    private readonly ICallGateProvider<string>? getJourney;
    private readonly ICallGateProvider<string, bool>? appendJourney;
    private readonly ICallGateProvider<string, bool>? controlJourney;
    private volatile string journeyJson = "{}";
    private volatile string routeJson = "{}";
    private double lastRoutePublished = double.NegativeInfinity;
    private Target? routeTarget;
    private bool routeWasValid;
    private readonly ICallGateProvider<string, uint, object>? arrivedEvent;
    private readonly ICallGateProvider<string, object>? stateChangedEvent;
    private delegate void OpenMapDelegate(AgentMap* agent, OpenMapInfo* info);

    private readonly Hook<OpenMapDelegate>? openMapHook;
    private readonly ArrivalTimer linkArrival = new();
    private bool commandRegistered;
    private bool aliasRegistered;

    // written by the framework update, read by Draw and by IPC callers on other threads
    private volatile Target? explicitTarget;
    private int pauseJourneyRequested;
    private sealed record FacingRequest(uint Territory);
    private FacingRequest? facingQuestRequested;
    private volatile WayfinderState state = new();
    private volatile uint territory;
    private Target? flag;
    private Target? quest;
    private Target? arrivalFor;
    private Target? eventArrivalFor;
    private StateChangeKey? publishedState;

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
        IAetheryteList aetherytes, IGameInteropProvider interop, IKeyState keyState, IPlayerState playerState)
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
            state = new WayfinderState
            {
                Enabled = config.Enabled, AutoTeleportEnabled = config.AutoTeleport,
                AutoTeleportMaxGil = config.AutoTeleportMaxGil,
                AutoTeleportStatus = config.AutoTeleport ? "Waiting for initial guidance." : "Automatic teleport is off.",
            };
            reader = new GameReader(data, aetherytes, log);
            navmesh = new Navmesh(pluginInterface, log);
            companionConnector = new GuideRouteConnector(pluginInterface);
            companionApproach = new GuideApproachRouter(pluginInterface);
            idleApproach = new GuideApproachRouter(pluginInterface);
            // Reuse the verified rendezvous walking engine; this instance only
            // requests idle positions and never observes or starts a Teleport.
            idleRouteMotion = new CompanionTeleportMotion(SelectIdleApproach);
            companionTeleportMotion = new CompanionTeleportMotion((zone, rendezvous, player, accepted, now, enabled) =>
                companionTeleport.Current.Mode == CompanionTeleportMode.Corral
                    ? companionApproach.SelectCorral(zone, rendezvous, player, accepted, now, enabled)
                    : companionApproach.Select(zone, rendezvous, player, accepted, now, enabled));
            companionJump = new CompanionJumpTraversal(companionJumpProbe.Probe, companionJumpProbe.ValidateStep);
            connections = new ZoneConnections(data, log);
            automaticTeleport = new AutomaticTeleport(aetherytes, condition, clientState, objects, gameGui, log, Print,
                pluginInterface.GetPluginConfigDirectory(), playerState);
            glove = new GloveTexture(textures, data, log);
            minion = new GloveMinion(data, objects, log);
            pointerAudio = new PointerAudio(log);
            worldDepth = new GuideWorldDepth(pluginInterface, log);
            imported = new ImportedGuide(pluginInterface, interop, textures, log, worldDepth);
            teleportBoarding = new GuideTeleportBoarding(clientState, objects, condition, imported);
            importedPreview = new ImportedGuide(pluginInterface, interop, textures, log, worldDepth);
            fairyTrail = new NativeFairyTrail(pluginInterface, interop, log);
            lastStyle = config.Style;
            overlay = new Overlay(gameGui, config, glove, navmesh, fairyTrail, log);
            terrainCheck = new TrailTerrainCheck(pluginInterface, gameGui, log);
            overlay.TerrainCheck = terrainCheck;
            window = new SettingsWindow(config, Save, () => state, () => glove.Status, () => config.GuideModelPath.Length > 0 ? imported.Status : minion.Status, () => navmesh.Status, () => Test(TestDistanceDefault), ClearFromUi, () => minion.Choices, () => automaticTeleport.Status, automaticTeleport.Retry);
            window.PreviewPointerSound = pointerAudio.Preview;
            modelLibrary = new ModelLibrary(config, Save, new GuideCatalog(data), () => minion.Status);
            modelLibrary.CanUsePreview = model => minion.IsReadyFor(model.Id, model.NpcId, model.BNpcId);
            visualAssets = new VisualAssetLibrary(textures);
            localModels = new LocalModelLibrary(config, Save, importedPreview,
                Path.Combine(pluginInterface.GetPluginConfigDirectory(), "ImportedArchives"));
            localModels.OpenExternalAssets = visualAssets.Open;
            modelLibrary.OpenLocalModels = OpenLocalModels;
            visualAssets.OpenLocalModels = OpenLocalModels;
            modelLibrary.OpenExternalAssets = visualAssets.Open;
            windowSystem.AddWindow(visualAssets);
            windowSystem.AddWindow(localModels);
            window.OpenModels = () => { CloseLocalPreview(); modelLibrary.Open(); };
            window.OpenLocalModels = OpenLocalModels;
            window.PreviewLocalFile = PreviewLocalFile;
            window.SelectFacingQuest = SelectFacingQuest;
            window.ImportedAnimations = () => imported.AnimationNames;
            window.ReloadImport = imported.Reload;
            windowSystem.AddWindow(modelLibrary);
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
            getRoute = pluginInterface.GetIpcProvider<string>(IpcContract.GetRoute);
            getRoute.RegisterFunc(() => routeJson);
            journeyJson = JourneyExchange.Snapshot(config.Journey);
            getJourney = pluginInterface.GetIpcProvider<string>(IpcContract.GetJourney);
            getJourney.RegisterFunc(() => journeyJson);
            appendJourney = pluginInterface.GetIpcProvider<string, bool>(IpcContract.AppendJourney);
            appendJourney.RegisterFunc(json => ChangeJourney(() => JourneyExchange.Append(config.Journey, json)));
            controlJourney = pluginInterface.GetIpcProvider<string, bool>(IpcContract.ControlJourney);
            controlJourney.RegisterFunc(action => ChangeJourney(() =>
            {
                var changed = JourneyExchange.Control(config.Journey, action);
                if (changed && action == "resume") config.Enabled = true;
                return changed;
            }));
            arrivedEvent = pluginInterface.GetIpcProvider<string, uint, object>(IpcContract.Arrived);
            stateChangedEvent = pluginInterface.GetIpcProvider<string, object>(IpcContract.StateChanged);

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
        if (Interlocked.Exchange(ref facingQuestRequested, null) is { } facingRequest)
        {
            if (player is null || !situation.HasPlayer || situation.BetweenAreas || situation.Cutscene
                || situation.GPose || zone == 0 || facingRequest.Territory != zone)
                Print("quest selection is unavailable during a character or zone transition; your destination is unchanged.");
            else
                SelectFacingQuestNow(zone, player.Position, player.Rotation);
        }
        var (visible, reason) = Visibility.Decide(situation, new VisibilityRules(config.Enabled, config.HideInCombat, config.HideInDuty));
        var now = Now;
        terrainCheck.Update(zone, player?.Position, visible, now);
        var dt = double.IsNaN(lastUpdate) ? 0f : (float)Math.Clamp(now - lastUpdate, 0, 0.25);
        lastUpdate = now;
        if (config.Style != lastStyle)
        {
            lastStyle = config.Style;
            minion.Retry();
        }

        if (player is null)
        {
            ResetCompanionRoute();
            automaticTeleport.Update(config, Guide.Nothing, false, now);
            eventArrival.Reset();
            eventArrivalFor = null;
            imported.Pose = null;
            importedPreview.Pose = null;
            minion.Update(MinionWant.Hard, null, config.MinionSize, config.MinionTilt, now, dt);
            pointerJourney.Hide(); pointerFrame = default;
            pointerAudio.Update(0, false, 0);
            navmesh.Idle();
            scene = default;
            PublishState(new WayfinderState
            {
                Enabled = config.Enabled, Visible = false, Reason = reason, Mode = config.Mode, Style = config.Style,
                AutoTeleportEnabled = config.AutoTeleport, AutoTeleportMaxGil = config.AutoTeleportMaxGil,
                AutoTeleportStatus = automaticTeleport.Status,
            });
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
            quest = config.UseQuest && mode is SourceMode.Auto or SourceMode.Quest ? reader.Quest(zone, position, linkQuest, config.QuestChoice) : null;
        }

        if (Interlocked.Exchange(ref pauseJourneyRequested, 0) != 0)
        {
            PauseJourney();
        }
        var target = config.Journey.Current ?? TargetPicker.Pick(mode, config.Toggles(), explicitTarget, flag, quest);
        var finalTarget = target;
        var connection = target is not null && target.TerritoryId != zone ? connections.Next(zone, target.TerritoryId, position) : null;
        if (connection is not null && target is not null)
            target = new Target(target.Source, zone, connection.Entrance.X, connection.Entrance.Z, connection.Entrance.Y, connection.Action, target.MainScenario);
        Vector3? aim = null;
        if (target is not null && target.TerritoryId == zone && config.UseNavmesh)
            aim = navmesh.Aim(zone, position, target.Position(position.Y), target.Y.HasValue, now);
        else
            navmesh.Idle();

        IReadOnlyList<AetheryteSpot> spots = target is not null && target.TerritoryId != zone && (config.AetheryteHint || config.AutoTeleport)
            ? reader.AetherytesIn(target.TerritoryId)
            : [];
        var guide = TargetPicker.Plan(zone, position, target, config.ArriveRadius, spots, aim);
        if (connection is not null)
            guide = guide with { FinalTarget = finalTarget, TransitAction = connection.Action, TransitTerritory = connection.To };
        automaticTeleport.Update(config, guide, visible, now);
        if (!Equals(guide.Target, eventArrivalFor))
        {
            eventArrival.Reset();
            eventArrivalFor = guide.Target;
        }
        var arrivedNow = guide.Target is not null && eventArrival.Update(guide.Kind == GuideKind.Arrived, now, 1.5);

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
        if (connection is null && config.Journey.Current is null && linkMode is not null && linkArrival.Update(guide.Kind == GuideKind.Arrived, now, 1.5))
        {
            EndLink();
            Print("you have arrived; following the usual way again.");
        }

        UpdateMinion(visible, guide, position, aim, now, dt);

        var targetZone = guide.Target is { } t ? reader.ZoneName(t.TerritoryId) : "";
        scene = new Scene(visible, guide, position, player.Rotation, aim, targetZone, companionRoutePose, clientState.TerritoryType,
            companionWaitingAside);
        PublishState(new WayfinderState
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
            JourneyIndex = config.Journey.Index,
            JourneyCount = config.Journey.Stops.Count,
            JourneyRunning = config.Journey.Running,
            AutoTeleportEnabled = config.AutoTeleport,
            AutoTeleportMaxGil = config.AutoTeleportMaxGil,
            AutoTeleportStatus = automaticTeleport.Status,
            RouteNote = aim is not null ? "" : config.UseNavmesh ? navmesh.Status : "switched off in the settings",
        });
        if (arrivedNow && guide.Target is { } reached)
            PublishArrived(reached);
    }

    private void PublishState(WayfinderState next)
    {
        state = next;
        var valid = next.Visible && next.Navmesh && next.Guide.Kind == GuideKind.Walk;
        if (Now - lastRoutePublished >= 0.25 || !Equals(routeTarget, next.Guide.Target) || valid != routeWasValid)
        {
            routeTarget = next.Guide.Target;
            routeWasValid = valid;
            lastRoutePublished = Now;
            var path = valid ? navmesh.Path : [];
            var chase = GuideChaseLayout.Create(config.Trail, config.MinionLeadDistance,
                config.MainScenarioStyle && next.Guide.Target is { MainScenario: true });
            // Export the independent companion's actual arc, including loyal return
            // behind the player. Only non-companion styles use a static layout.
            var routePose = valid ? companionRoutePose : null;
            if (valid && config.Style != PointerStyle.Minion) routePose = chase.Sample(path, navmesh.ProgressArc);
            var visual = routePose is { } pose ? RouteVisual.FromArcs(path, navmesh.ProgressArc,
                pose.GuideArc, pose.LookAheadArc, overlay.RouteColor(next.Guide.Target)) : null;
            routeJson = RouteSnapshot.Encode(next, path, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), visual);
        }
        var key = StateChangeKey.From(next);
        if (publishedState == key)
            return;
        publishedState = key;
        try
        {
            stateChangedEvent?.SendMessage(WayfinderIpc.StateJson(next));
        }
        catch (Exception ex)
        {
            // A subscriber must not be able to interrupt the pointer's update loop.
            log.Debug(ex, "XivWayfinder StateChanged subscriber failed");
        }
    }

    private void PublishArrived(Target target)
    {
        try
        {
            arrivedEvent?.SendMessage(WayfinderIpc.CleanLabel(target.Label), target.TerritoryId);
        }
        catch (Exception ex)
        {
            // A subscriber must not be able to interrupt the pointer's update loop.
            log.Debug(ex, "XivWayfinder Arrived subscriber failed");
        }
    }

    /// <summary>Framework-thread lifecycle reset; never retain an airborne clip after its model or route ownership ends.</summary>
    private void ResetCompanionRoute(bool removeVisual = false)
    {
        companionIntent.Reset();
        companionMotion.Reset();
        companionConnector.Reset();
        companionApproach.Reset();
        companionTeleport.Reset();
        idleRendezvous.Reset();
        idleApproach.Reset();
        idleMainRoute = null;
        idleTarget = null;
        idleContext++;
        companionWaitingAside = false;
        companionGesture.Reset();
        companionJump.Reset();
        companionJumpProbe.Reset();
        teleportBoarding?.Reset();
        companionRoutePose = null;
        companionAcceptedPose = null;
        imported.ResetRoutePlacement();
        minion.JumpFrame = imported.JumpFrame = importedPreview.JumpFrame = null;
        minion.GestureFrame = imported.GestureFrame = importedPreview.GestureFrame = null;
        if (removeVisual)
        {
            minion.Remove();
            imported.Pose = null;
            importedPreview.ResetRoutePlacement();
        }
    }

    private unsafe bool CanSeeDestination(Guide guide)
    {
        if (!config.DestinationPointer || guide.Kind is not (GuideKind.Walk or GuideKind.Arrived)
            || guide.Target is not { Y: not null } target || target.TerritoryId != clientState.TerritoryType) return false;
        var at = target.Position(0);
        if (!TrailPlacement.IsFinite(at) || !gameGui.WorldToScreen(at + Vector3.UnitY * .1f, out _, out var inside) || !inside) return false;
        var manager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
        var camera = manager == null ? null : manager->GetActiveCamera();
        if (camera == null) return false;
        Vector3 eye = camera->SceneCamera.Position;
        var toward = at + Vector3.UnitY * .2f - eye;
        var distance = toward.Length();
        if (!float.IsFinite(distance)) return false;
        var hit = distance > .05f ? TrailGround.Raycast(eye, toward / distance, distance) : null;
        return hit is null || Vector3.Distance(eye, hit.Value.Point) >= distance - .25f;
    }

    /// <summary>The minion glove: where it floats and which way it points, or why it should not be there.</summary>
    private void UpdateMinion(bool visible, Guide guide, Vector3 player, Vector3? aim, double now, float dt)
    {
        pointerFrame = default;
        companionRoutePose = null; // Never publish a previous route or hidden guide.
        companionWaitingAside = false;
        imported.RouteConstrained = false;
        importedPreview.Pose = null;
        minion.GestureFrame = imported.GestureFrame = importedPreview.GestureFrame = null;
        if (!visible || config.Style != PointerStyle.Minion || config.GuideGrounded || config.GuideModelPath.Length > 0
            || config.GuideNpcId != 0 || config.GuideBNpcId != 0 || localModels.Previewing || modelLibrary.IsOpen)
        { pointerJourney.Hide(); pointerAudio.Update(0, false, 0); }
        var identity = new CompanionVisualIdentity(config.GuideModelPath, config.GuideModelChara, config.GuideNpcId,
            config.MinionSize, config.GuideGrounded, config.GuideAnimate, config.GuideAnimation, config.GuideInPlace,
            visible && (localModels.Previewing || modelLibrary.IsOpen && modelLibrary.Preview is not null), config.GuideBNpcId);
        if (companionTerritory != clientState.TerritoryType || companionVisualIdentity != identity)
        {
            companionTerritory = clientState.TerritoryType;
            companionVisualIdentity = identity;
            ResetCompanionRoute(removeVisual: true);
            minion.Retry();
        }
        if (localModels.Previewing && visible)
        {
            imported.Pose = null;
            minion.Update(MinionWant.Hard, null, config.MinionSize, false, now, dt);
            ResetCompanionRoute();
            importedPreview.Grounded = localModels.PreviewGrounded;
            importedPreview.RouteConstrained = false;
            importedPreview.PreviewDirectPlacement = true;
            importedPreview.Update(localModels.PreviewPath);
            importedPreview.Pose = PreviewPose(player, localModels.PreviewGrounded, localModels.PreviewPace,
                localModels.PreviewTurn, now);
            return;
        }
        if (modelLibrary.IsOpen && modelLibrary.Preview is { } preview && visible)
        {
            ResetCompanionRoute();
            imported.Pose = null;
            minion.Select(preview.Id, preview.NpcId, preview.BNpcId);
            float radius = 0, height = 0;
            if (minion.IsReadyFor(preview.Id,preview.NpcId,preview.BNpcId)
                && minion.TryGetSpacingRadius(out var drawnRadius) && minion.TryGetDrawnSize(out var drawnSize))
            {
                if (modelLibrary.NeedsPreviewFit) modelLibrary.FitPreview(drawnSize);
                var ratio = modelLibrary.PreviewScale / Math.Max(.01f,nativePreviewDrawScale);
                radius = drawnRadius*ratio; height = drawnSize.Y*ratio;
            }
            var nativeRelocation = nativePreviewPlacement.Relocation;
            var previewPose = nativePreviewPlacement.Update(new(clientState.TerritoryType,"",preview.Id,preview.NpcId,
                preview.BNpcId,modelLibrary.PreviewScale,modelLibrary.PreviewGrounded),player,objects.LocalPlayer?.Rotation??0,
                modelLibrary.PreviewPace,modelLibrary.PreviewTurn,now,gameGui,radius,height,selectorBounds:modelLibrary.PreviewScreenBounds,log:log);
            modelLibrary.PreviewPlaced = previewPose is not null;
            modelLibrary.PreviewPlacementStatus = nativePreviewPlacement.Status;
            if (previewPose is not null && nativeRelocation != nativePreviewPlacement.Relocation) minion.Remove();
            minion.Update(previewPose is null ? MinionWant.Hard : MinionWant.Show,previewPose,
                modelLibrary.PreviewScale,false,now,dt,modelLibrary.PreviewGrounded,true,modelLibrary.PreviewTurn,directPlacement:true);
            nativePreviewDrawScale = modelLibrary.PreviewScale;
            if (modelLibrary.NeedsPreviewFit && minion.IsReadyFor(preview.Id, preview.NpcId, preview.BNpcId) && minion.TryGetDrawnSize(out var previewSize))
                modelLibrary.FitPreview(previewSize);
            return;
        }
        imported.Grounded = config.GuideGrounded;
        imported.Update(config.GuideModelPath);
        minion.Select(config.GuideModelChara, config.GuideNpcId, config.GuideBNpcId);
        var useImported = config.GuideModelPath.Length > 0;
        // The floating cursor is a companion even with no route, no destination,
        // or the trail switched off. Other models retain their route/ground ownership.
        if (!useImported && minion.IsCursor && config.GuideNpcId == 0 && config.GuideBNpcId == 0 && !config.GuideGrounded)
        {
            imported.Pose = null;
            minion.JumpFrame = null;
            var show = visible && config.Style == PointerStyle.Minion;
            var companion = show ? PointerPresentation.Companion(player, objects.LocalPlayer?.Rotation ?? 0,
                guide.Direction, config.MinionPlacement(), now, config.ReducedMotion) : null;
            var pointer = pointerJourney.Update(companion, guide.Target, show && CanSeeDestination(guide),
                guide.Kind == GuideKind.Arrived, player, now, dt, config.ReducedMotion);
            pointerFrame = pointer;
            minion.Update(pointer.Pose is not null ? MinionWant.Show : MinionWant.Hard,
                pointer.Pose, config.MinionSize, true, now, dt, animate: false, directPlacement: true);
            pointerAudio.Update(pointer.Hum, pointer.Chime, show && config.PointerSound ? config.PointerSoundVolume : 0);
            return;
        }
        pointerJourney.Hide(); pointerAudio.Update(0, false, 0);
        var gestureCanMove = useImported ? imported.GestureCanMove : minion.GestureCanMove;
        var teleportNeeded = guide.Kind == GuideKind.Teleport && guide.Target is { } teleportTarget
            && guide.Aetheryte is { Unlocked: true, Id: > 0 } teleportSpot
            && teleportSpot.TerritoryId == teleportTarget.TerritoryId;
        var teleportCasting = TryObserveTeleportCast(out var teleportRemaining);
        var teleportCue = companionTeleport.Update(new(clientState.TerritoryType, guide.Aetheryte?.Id ?? 0,
            player, objects.LocalPlayer?.Rotation ?? 0,
            companionMotion.Current.HasPosition ? companionMotion.Current.Position : null,
            visible && config.Style == PointerStyle.Minion && config.UseNavmesh,
            teleportNeeded, teleportCasting, teleportRemaining), now);
        teleportCue = teleportBoarding.Observe(teleportCue, companionMotion.Current,
            useImported && visible && config.Style == PointerStyle.Minion && config.UseNavmesh && config.GuideGrounded
                && config.GuideAnimate && config.GuideInPlace && string.IsNullOrWhiteSpace(config.GuideAnimation), config.MinionSize);
        if (teleportBoarding.Update(companionMotion, companionJump))
        {
            // A boarding visual has its own accepted pose, not a main-route
            // arc. Do not walk/rebase or publish the old takeoff arc in flight.
            companionMotion.HoldJumpClock(now);
            minion.Update(MinionWant.Hard, null, config.MinionSize, false, now, dt);
            return;
        }
        if (!teleportCue.Active && !companionJump.Active) companionApproach.Reset();
        var capability = visible && config.Style == PointerStyle.Minion && config.GuideGrounded && config.GuideAnimate
            && (companionJump.Active || teleportCue.Active || guide.Kind == GuideKind.Walk && navmesh.Following)
            && string.IsNullOrWhiteSpace(config.GuideAnimation) && (!useImported || config.GuideInPlace)
            ? useImported ? imported.GetJumpCapability(config.MinionSize) : minion.GetJumpCapability(config.MinionSize)
            : default;
        if (companionJump.Active && (companionJump.Frame is not { } ownedJump
            || !ownedJump.Plan.Valid || !ReferenceEquals(ownedJump.Plan.Route, companionMotion.Current.Route)
            || ownedJump.Plan.Request.Capability != capability
            || (useImported ? imported.JumpFrame is null || imported.LoadedPath.Length == 0 : !minion.Available)))
        {
            // Asset reloads and native body-bound changes invalidate an old
            // envelope. Hide/reseed; never lower an unverified airborne body.
            ResetCompanionRoute(removeVisual: true);
            return; // At least one hidden draw before a fresh grounded seed.
        }
        var previousImportedPose = imported.Pose;
        MinionWant want;
        MinionPose? pose = null;
        var routeConstrained = false;
        var motionSubmitted = false;
        var attention = 0f;
        var teleportArrived = false;
        var idleArrived = false;
        GuideGestureKind? gesture = null;
        GuideChaseLayout chase = default;
        if (!visible || config.Style != PointerStyle.Minion)
        {
            want = MinionWant.Hard;
            ResetCompanionRoute();
        }
        else if ((guide.Kind != GuideKind.Walk || guide.Target is null) && !companionJump.Active && !teleportCue.Active
            && gestureCanMove)
        {
            want = MinionWant.Soft;
            companionConnector.Reset();
            companionApproach.Reset();
            idleRendezvous.Reset();
            idleApproach.Reset();
        }
        else
        {
            var layout = config.MinionPlacement();
            if (config.GuideGrounded) layout = layout with { Height = 0, MaxPitchDegrees = 0 };
            chase = GuideChaseLayout.Create(config.Trail, config.MinionLeadDistance,
                config.MainScenarioStyle && guide.Target?.MainScenario == true);
            want = MinionWant.Hard;
            // Active guidance never invents a straight-line placement while
            // navmesh is missing/recalculating. Legacy beside preferences do
            // not override the requested autonomous route companion.
            var path = navmesh.Path;
            var playerRoute = navmesh.Following ? chase.WithLead(0).Sample(path, navmesh.ProgressArc) : null;
            if (!ReferenceEquals(idleMainRoute, path) || !Equals(idleTarget, guide.Target))
            {
                idleMainRoute = path; idleTarget = guide.Target; idleContext++;
                idleRendezvous.Reset(); idleApproach.Reset();
            }
            if (companionJump.Active || teleportCue.Active || playerRoute is not null || companionMotion.Current.HasPosition)
            {
                var intent = teleportCue.Active
                    ? new CompanionIntentFrame(CompanionIntentMode.Returning, 1, 0, true,
                        companionTeleport.ApproachCruiseSpeed)
                    : playerRoute is { } tracked
                    ? companionIntent.Update(player, navmesh.ProgressArc, tracked.Direction, now, chase.GuideLead, true)
                    : companionIntent.Current;
                var measuredRadius = useImported ? imported.TryGetSpacingRadius(config.MinionSize, out var guideRadius)
                    : minion.TryGetSpacingRadius(out guideRadius);
                var idleRequest = idleRendezvous.Update(new(clientState.TerritoryType, idleContext, player,
                    objects.LocalPlayer?.Rotation ?? 0, config.MinionSide, measuredRadius ? guideRadius : float.NaN,
                    objects.LocalPlayer?.HitboxRadius ?? float.NaN, intent.Mode == CompanionIntentMode.Waiting,
                    playerRoute is not null && companionMotion.Current.HasPosition && gestureCanMove
                        && !teleportCue.Active && !companionJump.Active
                        && (useImported ? imported.Available : minion.Available)), now);
                if (idleRequest is null) idleApproach.Reset();
                companionWaitingAside = idleRequest is not null;
                if (idleRequest is { } requested && loggedIdleRequest != requested.Generation)
                {
                    loggedIdleRequest = requested.Generation;
                    log.Information("Wayfinder idle side request: player={Player}, goal={Goal}, guideRadius={Radius}, minimumClearance={Clearance}.",
                        player, requested.Goal, guideRadius, requested.MinimumClearance);
                }
                CompanionRouteMotionFrame motion;
                if (teleportCue.Active)
                {
                    companionConnector.Reset();
                    var approaching = companionTeleportMotion.Update(companionMotion, companionJump,
                        clientState.TerritoryType, player, teleportCue, intent, capability, now,
                        gestureCanMove);
                    motion = approaching.Motion;
                    motionSubmitted = approaching.MotionSubmitted;
                    teleportArrived = approaching.AtDestination;
                }
                else if (companionJump.Active)
                {
                    // The already-approved route owns the whole jump, even
                    // while the player's main route is being recalculated.
                    // Connector selection/rebase would cancel that ownership.
                    motion = companionJump.Update(companionMotion, clientState.TerritoryType,
                        companionMotion.Current.Route, navmesh.ProgressArc, intent, capability, now, true);
                    motionSubmitted = true;
                }
                else if (idleRequest is { } resting)
                {
                    companionConnector.Reset();
                    var destination = new CompanionTeleportFrame(CompanionTeleportMode.RequestStop, resting.Goal,
                        player, resting.Generation, false, false, false);
                    var approaching = idleRouteMotion.Update(companionMotion, companionJump,
                        clientState.TerritoryType, player, destination,
                        new CompanionIntentFrame(CompanionIntentMode.Returning, 0, 0, true, 1.5f),
                        capability, now, gestureCanMove);
                    motion = approaching.Motion;
                    motionSubmitted = approaching.MotionSubmitted;
                    idleArrived = approaching.AtDestination;
                }
                else if (playerRoute is null || !gestureCanMove)
                {
                    // Keep the last accepted location while a route is pending
                    // or an authored stand-up finishes; never slide a seated rig.
                    motion = HoldCompanionMotion(now);
                }
                else
                {
                    var selected = companionConnector.Select(clientState.TerritoryType, path, navmesh.ProgressArc,
                        intent, companionMotion.Current, now, true);
                    // A connector supplies an exact route arc at each handoff.
                    // Never globally project a crossing route or fabricate a leg.
                    var rebased = selected.RebaseArc is not { } atArc || companionMotion.TryRebase(selected.Route, atArc);
                    motion = rebased
                        ? companionJump.Update(companionMotion, clientState.TerritoryType, selected.Route,
                            selected.ProgressArc, selected.Intent, capability, now, true)
                        : companionMotion.Current with { Velocity = 0, NeedsConnection = true };
                    motionSubmitted = rebased;
                }
                if (motion.HasPosition && chase.SampleAtArc(motion.Route, motion.Arc) is { } sampled)
                {
                    var onCurrentRoute = !motion.NeedsConnection && ReferenceEquals(motion.Route, path);
                    // An unmatched re-path holds its old verified pose while
                    // awaiting a real connector. Never export the old arc as
                    // though it belongs to the new route.
                    var jumpFrame = companionJump.Frame;
                    var reverse = jumpFrame is { } airborne
                        ? airborne.Plan.Request.EndArc < airborne.Plan.Request.StartArc
                        : motion.Velocity < -.03f || Math.Abs(motion.Velocity) <= .03f
                            && intent.Mode != CompanionIntentMode.Lead && onCurrentRoute
                            && navmesh.ProgressArc < motion.Arc;
                    var facing = sampled.Direction;
                    if (reverse)
                    {
                        // At an exact corner a returning guide follows the
                        // incoming segment, not the outgoing forward tangent.
                        var behind = chase.SampleAtArc(motion.Route, Math.Max(0, MathF.BitDecrement(motion.Arc)));
                        facing = -(behind?.Direction ?? facing);
                    }
                    attention = jumpFrame is null && onCurrentRoute && !config.ReducedMotion
                        ? intent.GoalGlanceWeight * (intent.Mode == CompanionIntentMode.Lead
                            ? GuideChaseLayout.Attention(now, false) : 1) : 0;
                    var travelYaw = MinionGlove.Yaw(facing, layout.TurnDegrees);
                    var lightYaw = MinionGlove.Yaw(sampled.LightDirection, layout.TurnDegrees);
                    var facingYaw = GuideChaseLayout.AttentiveYaw(travelYaw, lightYaw, attention);
                    if ((teleportArrived || idleArrived) && jumpFrame is null && Math.Abs(motion.Velocity) <= .03f
                        && CanSeeTeleportPlayer(sampled.Position, player))
                    {
                        // Looking at the nearby player is a stop request, not
                        // a crow-flies direction toward a remote destination.
                        facingYaw = MinionGlove.Yaw(Heading.DirectionTo(sampled.Position, player), layout.TurnDegrees);
                        if (config.GuideGrounded && config.GuideAnimate && string.IsNullOrWhiteSpace(config.GuideAnimation)
                            && (!useImported || config.GuideInPlace))
                        {
                            var gestures = useImported ? imported.GetGestureCapabilities() : minion.GetGestureCapabilities();
                            gesture = teleportCue.RequestRest && !config.AutoTeleport && gestures.SitGround
                                ? GuideGestureKind.SitGround
                                : teleportCue.RequestBeckon && gestures.Beckon ? GuideGestureKind.Beckon
                                : gestures.Wait ? GuideGestureKind.Wait : null;
                        }
                    }
                    var bob = !config.GuideGrounded && !config.ReducedMotion ? Pulse.Bob(now, 2.2f, .08f) : 0;
                    var at = jumpFrame?.FeetPosition ?? sampled.Position + Vector3.UnitY * (layout.Height + bob);
                    if (jumpFrame is null)
                    {
                        guideGround.Begin(clientState.TerritoryType);
                        if (guideGround.TryPlace(sampled.Position, now, out var floor))
                            at.Y = MathF.Max(at.Y, floor.Y + layout.Height);
                    }
                    pose = new(at, facingYaw, MinionGlove.Pitch(at, sampled.LookAt, true, layout.MaxPitchDegrees));
                    routeConstrained = true;
                    want = MinionWant.Show;
                }
            }
            else
            {
                companionConnector.Reset();
                companionJumpProbe.Reset();
                companionIntent.Update(player, 0, Vector2.Zero, now, chase.GuideLead, false);
                companionJump.Update(companionMotion, clientState.TerritoryType, [], 0, companionIntent.Current, default, now, false);
                companionJump.ResolvePlacement(companionMotion, true);
            }
        }

        var proposedJump = routeConstrained ? companionJump.Frame : null;
        if (!gestureCanMove && proposedJump is null)
        {
            // Stand up at the accepted root/yaw, not a rejected new facing.
            // A null request independently starts exit; placement refusal must
            // never restore a paused Sit request and trap the actor seated.
            if (gesture != GuideGestureKind.SitGround) gesture = null;
            if (routeConstrained && companionAcceptedPose is { } seated) pose = seated;
        }
        imported.RouteConstrained = routeConstrained;
        imported.JumpFrame = useImported ? proposedJump : null;
        minion.JumpFrame = useImported ? null : proposedJump;
        imported.Pose = useImported && want == MinionWant.Show ? pose : null;
        var proposedGesture = companionGesture.Propose(routeConstrained && proposedJump is null ? gesture : null, now);
        imported.GestureFrame = useImported ? proposedGesture : null;
        minion.GestureFrame = useImported ? null : proposedGesture;
        var importedAccepted = useImported && imported.Pose is { } proposedImportedPose && imported.LoadedPath.Length > 0
            && (proposedJump is { } importedJump ? imported.CanRenderJumpFrame(importedJump, config.MinionSize)
                : !config.GuideGrounded || imported.CanRenderRoutePose(proposedImportedPose, config.MinionSize));
        minion.Update(useImported ? MinionWant.Hard : want, pose, config.MinionSize, config.MinionTilt, now, dt,
            config.GuideGrounded, config.GuideAnimate, config.MinionTurn, routeConstrained);
        if (routeConstrained)
        {
            var accepted = useImported ? importedAccepted : minion.PlacementAccepted;
            if (accepted && pose is { } acceptedPose) companionAcceptedPose = acceptedPose;
            var acceptedGesture = companionGesture.Resolve(accepted);
            imported.GestureFrame = useImported ? acceptedGesture : null;
            minion.GestureFrame = useImported ? null : acceptedGesture;
            var placed = motionSubmitted ? companionJump.ResolvePlacement(companionMotion, accepted)
                : accepted ? companionMotion.Current : HoldCompanionMotion(now);
            if (!accepted)
            {
                // Preflight may have overwritten the imported vertex cache;
                // restore the exact accepted clip before a later UI draw.
                var retained = companionJump.Frame;
                if (useImported)
                {
                    imported.JumpFrame = retained;
                    imported.Pose = retained is { } held
                        ? imported.CanRenderJumpFrame(held, config.MinionSize) && (previousImportedPose ?? pose) is { } previous
                            ? previous with { Position = held.FeetPosition } : null
                        : previousImportedPose is { } groundedPrevious && (!config.GuideGrounded
                            || imported.CanRenderRoutePose(groundedPrevious, config.MinionSize)) ? groundedPrevious : null;
                }
                else minion.JumpFrame = retained; // Native refusal kept and froze its preceding pose.
            }
            // An accepted Complete frame remains on the renderer for this draw,
            // even though the traversal relinquishes ownership for next update.
            // Only the matching current-route arc may reach trail/map consumers.
            companionRoutePose = motionSubmitted && (useImported ? imported.Available && imported.Pose is not null : minion.Available)
                && placed.HasPosition && !placed.NeedsConnection
                && ReferenceEquals(placed.Route, navmesh.Path)
                ? chase.SampleAtArc(placed.Route, placed.Arc) : null;
        }
    }

    private CompanionRouteMotionFrame HoldCompanionMotion(double now)
    {
        companionJump.ResolvePlacement(companionMotion, false);
        return companionMotion.HoldJumpClock(now);
    }

    private CompanionApproachSelection SelectIdleApproach(uint zone, Vector3 goal, Vector3 player,
        CompanionRouteMotionFrame accepted, double now, bool enabled)
    {
        var selected = idleApproach.Select(zone, goal, player, accepted, now, enabled);
        if (!selected.Ready) return selected;
        if (idleRendezvous.Current is { } request && selected.Route is { Length: >= 2 })
        {
            var clear = idleRendezvous.AcceptsQueriedEndpoint(request, selected.Route[^1], player);
            if (loggedIdleRoute != request.Generation || loggedIdleClear != clear)
            {
                loggedIdleRoute = request.Generation;
                loggedIdleClear = clear;
                log.Information("Wayfinder idle side route: accepted={Accepted}, corners={Corners}, endpoint={Endpoint}, playerDistance={Distance}, minimumClearance={Clearance}.",
                    clear, selected.Route.Length, selected.Route[^1], Heading.FlatDistance(selected.Route[^1], player), request.MinimumClearance);
            }
            if (clear) return selected;
        }
        // A complete navmesh result may still stop inward of the requested side
        // point. Its normal endpoint tolerance must not erase body clearance.
        idleApproach.Reset(); // Retains the query cooldown, but releases the refused endpoint.
        return new(accepted.Route ?? [], accepted.Arc, null, true, false, false);
    }

    private bool TryObserveTeleportCast(out float remaining)
    {
        remaining = 0;
        var player = objects.LocalPlayer;
        if (!clientState.IsLoggedIn || clientState.TerritoryType == 0 || player is null
            || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]
            || condition[ConditionFlag.LoggingOut]) return false;
        return TeleportCastObservation.TryRemaining(player.IsCasting, player.CastActionType, player.CastActionId,
            player.CurrentCastTime, player.TotalCastTime, out remaining);
    }

    private static bool CanSeeTeleportPlayer(Vector3 guide, Vector3 player)
    {
        if (!TrailPlacement.IsFinite(guide) || !TrailPlacement.IsFinite(player)
            || Heading.FlatDistance(guide, player) > 3 || Math.Abs(guide.Y-player.Y) > 1) return false;
        var from = guide + Vector3.UnitY*.5f;
        var delta = player + Vector3.UnitY*.5f - from;
        var distance = delta.Length();
        if (distance < .05f) return true;
        var hit = TrailGround.Raycast(from, delta/distance, distance);
        return hit is null || Vector3.Distance(from, hit.Value.Point) >= distance-.05f;
    }

    private void DrawUi()
    {
        fairyTrail.Clear();
        try
        {
            windowSystem.Draw();
            localModels.DrawFiles();
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
            var modelPreview = frame.Visible && (localModels.Previewing || modelLibrary.IsOpen && modelLibrary.Preview is not null);
            overlay.Draw(frame, modelPreview);
            if (frame.Visible && !modelPreview && pointerFrame.Pose is not null && minion.CursorNeedsLoadingFallback)
            {
                var fallback = PointerPresentation.Companion(frame.Player, frame.Rotation, frame.Guide.Direction,
                    config.MinionPlacement(), Now, config.ReducedMotion);
                if (fallback is { } pose) overlay.DrawLoadingPointer(pose);
            }
            imported.Queue(config.MinionSize, config.GuideAnimate, config.GuideAnimation, config.GuideInPlace);
        }
        importedPreview.Queue(localModels.PreviewScale, localModels.PreviewAnimate,
            localModels.PreviewAnimation, localModels.PreviewInPlace);
    }

    private MinionPose? PreviewPose(Vector3 player, bool grounded, float pace, float turn, double now)
    {
        importedPreview.TryGetPreviewBounds(localModels.PreviewScale,out var radius,out var height,out var minimum);
        var relocation = localPreviewPlacement.Relocation;
        var pose = localPreviewPlacement.Update(new(clientState.TerritoryType,localModels.PreviewPath,0,0,0,
            localModels.PreviewScale,grounded),player,objects.LocalPlayer?.Rotation??0,pace,turn,now,gameGui,radius,height,minimum,
            selectorBounds:localModels.PreviewScreenBounds,log:log);
        localModels.PreviewPlacementStatus = localPreviewPlacement.Status;
        // The renderer's normal smoothing must not join two independently safe
        // preview spots by an unchecked chord through the room's wall.
        if (pose is null || relocation != localPreviewPlacement.Relocation) importedPreview.ResetRoutePlacement();
        return pose;
    }

    private void OpenLocalModels() { modelLibrary.IsOpen = false; modelLibrary.StopPreview(); localModels.Open(); }
    private void PreviewLocalFile(string path) { OpenLocalModels(); localModels.PreviewFile(path); }
    private void CloseLocalPreview() { localModels.IsOpen = false; localModels.OnClose(); }

    private void OpenUi() => window.IsOpen = true;

    private void OnCommand(string command, string arguments)
    {
        try
        {
            if (arguments.Trim().Equals("models", StringComparison.OrdinalIgnoreCase)) { CloseLocalPreview(); modelLibrary.Open(); return; }
            if (arguments.StartsWith("models ", StringComparison.OrdinalIgnoreCase)) { CloseLocalPreview(); modelLibrary.Open(arguments[7..]); return; }
            if (arguments.Trim().Equals("assets", StringComparison.OrdinalIgnoreCase)) { visualAssets.Open(); return; }
            if (arguments.StartsWith("assets ", StringComparison.OrdinalIgnoreCase)) { visualAssets.Open(arguments[7..].Trim()); return; }
            if (arguments.Trim().Equals("terraincheck", StringComparison.OrdinalIgnoreCase)
                || arguments.StartsWith("terraincheck ", StringComparison.OrdinalIgnoreCase))
            {
                Print(terrainCheck.Command(arguments.Length > 12 ? arguments[12..] : "", clientState.TerritoryType,
                    objects.LocalPlayer?.Position, Now));
                return;
            }
            if (arguments.Trim().Equals("localmodels", StringComparison.OrdinalIgnoreCase)) { OpenLocalModels(); return; }
            if (arguments.StartsWith("preview ", StringComparison.OrdinalIgnoreCase))
            {
                var file = arguments[8..].Trim().Trim('"');
                if (file.Equals("stop", StringComparison.OrdinalIgnoreCase)) CloseLocalPreview();
                else PreviewLocalFile(file);
                return;
            }
            if (arguments.Trim().Equals("beside", StringComparison.OrdinalIgnoreCase))
            {
                config.MinionLeadDistance = 2; Save();
                Print("Closer route lead selected (two yalms with the trail on). Guides now navigate independently instead of locking to your shoulder; without a trail they still take point."); return;
            }
            if (arguments.StartsWith("trail ", StringComparison.OrdinalIgnoreCase))
            {
                var value = arguments[6..].Trim();
                if (value.Equals("ground", StringComparison.OrdinalIgnoreCase)) config.TrailElevation = TrailElevation.Ground;
                else if (value.Equals("overhead", StringComparison.OrdinalIgnoreCase)) config.TrailElevation = TrailElevation.Overhead;
                else if (value.Equals("fairy", StringComparison.OrdinalIgnoreCase)) config.FairyTrail = true;
                else if (value.Equals("line", StringComparison.OrdinalIgnoreCase)) config.FairyTrail = false;
                else { Print("Use /wayfinder trail ground|overhead|fairy|line."); return; }
                config.Trail = true; Save(); Print($"Trail: {(config.FairyTrail ? "Fairy glow" : "Classic line")}, {config.TrailElevation}."); return;
            }
            if (arguments.StartsWith("animation ", StringComparison.OrdinalIgnoreCase))
            {
                var name=arguments[10..].Trim();
                config.GuideAnimation=name.Equals("auto",StringComparison.OrdinalIgnoreCase) ? "" : name;
                Save(); Print("Imported animation: " + (config.GuideAnimation.Length==0 ? "automatic" : config.GuideAnimation)); return;
            }
            if (arguments.StartsWith("import ", StringComparison.OrdinalIgnoreCase))
            {
                var file = arguments[7..].Trim().Trim('"').Trim();
                if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    PreviewLocalFile(file);
                    Print("Choose a model from the downloaded pack, then choose Use this guide.");
                    return;
                }
                config.GuideModelPath = file; imported.Reload();
                config.Style = PointerStyle.Minion; Save(); Print("Loading guide model: " + config.GuideModelPath); return;
            }
            if (arguments.Equals("pointerstatus", StringComparison.OrdinalIgnoreCase))
            {
                log.Information("Pointer status: {Status}", System.Text.Json.JsonSerializer.Serialize(new {
                    singlePointer = true, pointerFrame.AtDestination, pointerFrame.Hum,
                    position = pointerFrame.Pose is { } p ? new[] { p.Position.X, p.Position.Y, p.Position.Z } : null,
                    native = minion.Status, placed = minion.PlacementAccepted, trail = config.Trail,
                    sound = config.PointerSound, volume = config.PointerSoundVolume })); return;
            }
            if (arguments.Equals("soundtest", StringComparison.OrdinalIgnoreCase)) { pointerAudio.Preview(); Print("Previewing the approach hum and arrival bell at your pointer sound volume."); return; }
            if (arguments.StartsWith("model ", StringComparison.OrdinalIgnoreCase))
            {
                var name = arguments[6..].Trim();
                if (name.Equals("pointer", StringComparison.OrdinalIgnoreCase)) name = "wind-up cursor";
                if (name.Equals("moogle", StringComparison.OrdinalIgnoreCase)) name = "wind-up moogle";
                if (name.Equals("chocobo", StringComparison.OrdinalIgnoreCase)) name = "black chocobo chick";
                var choice = minion.Choices.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (choice.Model == 0) choice = minion.Choices.FirstOrDefault(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (choice.Model == 0) { Print("No minion matches that name. Choose a guide in /wayfinder."); return; }
                config.GuideNpcId = config.GuideBNpcId = 0; config.GuideModelChara = choice.Model; config.GuideModelPath = ""; config.Style = PointerStyle.Minion;
                config.GuideGrounded = name.Contains("chocobo", StringComparison.OrdinalIgnoreCase);
                config.MinionWithBead = false;
                config.MinionHeight = 2f;
                config.MinionTilt = false;
                config.MinionSize = name.Contains("cursor", StringComparison.OrdinalIgnoreCase) ? 2f : 1.5f;
                if (MinionGlove.IsWindUpCursor(choice.Name))
                {
                    config.MinionAhead = 1.6f; config.MinionSide = 0; config.MinionHeight = 2.4f;
                    config.MinionTilt = true; config.MinionTurn = 0; config.DestinationPointer = true;
                }
                Save(); Print($"Guide: {choice.Name}."); return;
            }
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
                case Verb.FaceQuest:
                    SelectFacingQuest();
                    break;
                case Verb.MapTarget:
                    SetMapTargetFromCommand(request);
                    break;
                case Verb.Mode:
                    PauseJourney();
                    EndLink();
                    config.Mode = request.Mode;
                    Save();
                    Print("following " + WayfinderIpc.Name(request.Mode) + (request.Mode == SourceMode.Auto ? ": target, then flag, then quest." : " only."));
                    break;
                case Verb.Style:
                    config.Style = request.Style;
                    Save();
                    Print(request.Style == PointerStyle.Minion
                        ? "Your chosen model guide is active, shown only to you."
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
                case Verb.AutoTeleport:
                    if (request.Name == "retry")
                    {
                        automaticTeleport.Retry();
                        Print(config.AutoTeleport ? "automatic Teleport retry requested; safety checks and the 30-second cooldown still apply." : "automatic Teleport is off; enable it before retrying.");
                    }
                    else
                    {
                        config.AutoTeleport = request.Name == "on";
                        Save();
                        Print(config.AutoTeleport
                            ? $"automatic Teleport enabled (normal gil costs, up to {config.AutoTeleportMaxGil:N0} gil per cast); waits until safely idle."
                            : "automatic Teleport off.");
                    }
                    break;
                case Verb.SavePlace:
                    SavePlace(request.Name);
                    break;
                case Verb.GoPlace:
                    GoPlace(request.Name);
                    break;
                case Verb.ForgetPlace:
                    ForgetPlace(request.Name);
                    break;
                case Verb.ListPlaces:
                    ListPlaces();
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "{Command} failed", Command);
        }
    }

    private void SelectFacingQuest() => Interlocked.Exchange(ref facingQuestRequested, new FacingRequest(territory));

    private void SelectFacingQuestNow(uint zone, Vector3 player, float rotation)
    {
        var selected = FacingQuestSelection.Choose(reader.QuestCandidates(zone, player), zone,
            player, rotation, config.ArriveRadius);
        if (selected is not { } choice)
        {
            Print("no known local quest step within 25 degrees ahead; your destination is unchanged. Face its destination and use /wayfinder face.");
            return;
        }
        PauseJourney();
        linkMode = SourceMode.Quest;
        linkQuest = choice.Quest.Lead.QuestId;
        linkArrival.Reset();
        lastScan = double.NegativeInfinity;
        config.UseQuest = true;
        config.Enabled = true;
        Save();
        Print($"following {WayfinderIpc.CleanLabel(choice.Quest.Place.Label)} ({choice.Distance:0} yalms). The guide will take the walkable route; ordinary turning keeps this choice.");
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
        PauseJourney();
        EndLink();
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

    private void SavePlace(string name)
    {
        if (objects.LocalPlayer is not { } player || clientState.TerritoryType == 0)
        {
            Print("no character position to save.");
            return;
        }

        var zone = clientState.TerritoryType;
        if (reader.IsHousing(zone))
        {
            Print("places inside housing are not saved yet: a ward, plot and room are needed to find the same place again.");
            return;
        }

        if (!SavedPlaces.Store(config.Places, new SavedPlace(name, zone, player.Position.X, player.Position.Y, player.Position.Z), out var replaced, out var error))
        {
            Print(error + ".");
            return;
        }

        Save();
        var clean = config.Places[SavedPlaces.Find(config.Places, name)].Name;
        Print($"{(replaced ? "updated" : "saved")} {clean} in {reader.ZoneName(zone)}.");
    }

    private void GoPlace(string name)
    {
        var index = SavedPlaces.Find(config.Places, name);
        if (index < 0)
        {
            Print($"no saved place called \"{SavedPlaces.CleanName(name)}\"; /wayfinder places lists them.");
            return;
        }

        var place = config.Places[index];
        UseExplicit(new Target(TargetSource.Explicit, place.TerritoryId, place.X, place.Z, place.Y, place.Name));
        Print($"pointing at {place.Name} in {reader.ZoneName(place.TerritoryId)}.");
    }

    private void ForgetPlace(string name)
    {
        var clean = SavedPlaces.CleanName(name);
        if (!SavedPlaces.Forget(config.Places, clean))
        {
            Print($"no saved place called \"{clean}\".");
            return;
        }

        Save();
        Print($"forgot {clean}.");
    }

    private void ListPlaces()
    {
        if (config.Places.Count == 0)
        {
            Print("no saved places; stand somewhere and use /wayfinder save NAME.");
            return;
        }

        Print($"saved places ({config.Places.Count}):");
        foreach (var place in config.Places.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Print($"{place.Name} — {reader.ZoneName(place.TerritoryId)}");
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
        Interlocked.Exchange(ref pauseJourneyRequested, 1);
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
        var had = explicitTarget is not null || linkMode is not null || config.Journey.Running;
        Interlocked.Exchange(ref pauseJourneyRequested, 1);
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
        PauseJourney();
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

    private void PauseJourney()
    {
        if (!config.Journey.Running) return;
        config.Journey.Running = false;
        Save();
    }

    private void ClearFromUi()
    {
        PauseJourney();
        Print(ClearExplicit() ? "target cleared." : "there was no /wayfinder target to clear.");
    }

    private bool ChangeJourney(Func<bool> change)
    {
        // Do not mutate the player's list from an IPC caller's worker thread.
        // Callers must marshal edits through their own framework service.
        if (!framework.IsInFrameworkUpdateThread || !change()) return false;
        Save();
        return true;
    }

    private void Save()
    {
        journeyJson = JourneyExchange.Snapshot(config.Journey);
        pluginInterface.SavePluginConfig(config);
    }

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
            { minion.Remove(); }
            else if (!framework.RunOnFrameworkThread(() => { minion.Remove(); }).Wait(TimeSpan.FromSeconds(2)))
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
        getRoute?.UnregisterFunc();
        getJourney?.UnregisterFunc();
        appendJourney?.UnregisterFunc();
        controlJourney?.UnregisterFunc();
        framework.Update -= OnUpdate;
        openMapHook?.Dispose();
        pointerAudio?.Dispose();
        RemoveMinion();
        pluginInterface.UiBuilder.Draw -= DrawUi;
        pluginInterface.UiBuilder.OpenMainUi -= OpenUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenUi;
        windowSystem.RemoveAllWindows();
        visualAssets?.Dispose();
        localModels?.Dispose();
        importedPreview?.Dispose();
        fairyTrail?.Dispose();
        imported?.Dispose();
        worldDepth?.Dispose();
        companionConnector?.Dispose();
        companionApproach?.Dispose();
        idleApproach?.Dispose();
        terrainCheck?.Dispose();
        navmesh?.Dispose();
    }
}
