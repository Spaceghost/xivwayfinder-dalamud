using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

/// <summary>/wayfinder: what to follow, what the pointer looks like, when it steps aside.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly Configuration config;
    private readonly Action save;
    private readonly Func<WayfinderState> state;
    private readonly Func<string> gloveStatus;
    private readonly Func<string> minionStatus;
    private readonly Func<string> navmeshStatus;
    private readonly Func<string> teleportStatus;
    private readonly Action retryTeleport;
    private readonly Action test;
    private readonly Action clear;
    private bool confirmClearJourney;
    private string modelPathDraft;
    public Action? ReloadImport { get; set; }
    public Func<string[]>? ImportedAnimations { get; set; }
    public Action? OpenModels { get; set; }
    public Action? OpenLocalModels { get; set; }
    public Action<string>? PreviewLocalFile { get; set; }
    public Action? SelectFacingQuest { get; set; }
    private readonly Func<IReadOnlyList<(string Name, uint Model)>> modelChoices;

    public SettingsWindow(Configuration config, Action save, Func<WayfinderState> state, Func<string> gloveStatus, Func<string> minionStatus, Func<string> navmeshStatus, Action test, Action clear, Func<IReadOnlyList<(string Name, uint Model)>> modelChoices, Func<string> teleportStatus, Action retryTeleport)
        : base("XivWayfinder###WayfinderSettings")
    {
        this.config = config;
        modelPathDraft = config.GuideModelPath;
        this.save = save;
        this.state = state;
        this.gloveStatus = gloveStatus;
        this.minionStatus = minionStatus;
        this.navmeshStatus = navmeshStatus;
        this.teleportStatus = teleportStatus;
        this.retryTeleport = retryTeleport;
        this.test = test;
        this.clear = clear;
        this.modelChoices = modelChoices;
        Size = new Vector2(580, 650);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var changed = false;
        var s = state();
        ImGui.TextWrapped(WayfinderIpc.StatusLine(s));
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(.42f,.83f,.79f,1), "A COMPANION FOR THE JOURNEY");
        if (ImGui.Button("Test: a target 20 yalms ahead"))
            test();
        ImGui.SameLine();
        if (ImGui.Button("Clear target"))
            clear();

        ImGui.Separator();
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Show the pointer", ref enabled))
        {
            config.Enabled = enabled;
            changed = true;
        }

        if (!ImGui.BeginTabBar("WayfinderSections")) return;
        if (ImGui.BeginTabItem("Journey"))
        {
        DrawJourney(s);
        ImGui.TextUnformatted("Follow");
        changed |= Radio("Everything, by priority", SourceMode.Auto, config.Mode, v => config.Mode = v);
        ImGui.SameLine();
        changed |= Radio("Target##mode", SourceMode.Explicit, config.Mode, v => config.Mode = v);
        ImGui.SameLine();
        changed |= Radio("Flag##mode", SourceMode.Flag, config.Mode, v => config.Mode = v);
        ImGui.SameLine();
        changed |= Radio("Quest##mode", SourceMode.Quest, config.Mode, v => config.Mode = v);
        changed |= Check("Targets from /wayfinder and other plugins", config.UseExplicit, v => config.UseExplicit = v);
        changed |= Check("The map flag", config.UseFlag, v => config.UseFlag = v);
        changed |= Check("A quest's next step", config.UseQuest, v => config.UseQuest = v);
        if (ImGui.Button("Follow the quest I'm facing")) SelectFacingQuest?.Invoke();
        ImGui.TextWrapped("Face a known quest destination in this zone, then select it here or use /wayfinder face. Ordinary turning never changes your destination; navigation still follows the walkable route.");
        if (config.UseQuest)
        {
            ImGui.Indent();
            changed |= Radio("The nearest##quest", QuestChoice.Nearest, config.QuestChoice, v => config.QuestChoice = v);
            ImGui.SameLine();
            changed |= Radio("The tracked one first##quest", QuestChoice.Tracked, config.QuestChoice, v => config.QuestChoice = v);
            changed |= Check("Main Scenario steps stand out (amber, labelled, an eager glove)", config.MainScenarioStyle, v => config.MainScenarioStyle = v);
            ImGui.Unindent();
        }
        changed |= Check("In another zone, name the nearest aetheryte", config.AetheryteHint, v => config.AetheryteHint = v);
        changed |= Check("Automatically teleport when guidance says to (spends gil)", config.AutoTeleport, v => config.AutoTeleport = v);
        if (config.AutoTeleport)
        {
            ImGui.Indent();
            var maxGil = (int)config.AutoTeleportMaxGil;
            if (ImGui.InputInt("Maximum gil per teleport (0: free only)", ref maxGil))
            {
                config.AutoTeleportMaxGil = (uint)Math.Clamp(maxGil, 0, 100_000);
                changed = true;
            }
            ImGui.TextWrapped(teleportStatus());
            ImGui.TextWrapped("Uses the game's Teleport spell and normal gil costs. Waits until you are safely idle and still; never walks for you. Interrupted/refused casts are not retried automatically.");
            if (ImGui.Button("Retry automatic teleport")) retryTeleport();
            ImGui.Unindent();
        }
        changed |= Check("Forget a /wayfinder target once reached", config.ClearOnArrival, v => config.ClearOnArrival = v);
        changed |= Check("Map links lead the way instead of opening the map (Shift: the map)", config.MapLinks, v => config.MapLinks = v);
        if (config.MapLinks)
            changed |= Check("  ...with the glove minion leading", config.MapLinksMinion, v => config.MapLinksMinion = v);

        ImGui.Separator();
        ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Guide"))
        {
        ImGui.TextUnformatted("Your travelling companion");
        changed |= Radio("World model guide", PointerStyle.Minion, config.Style, v => config.Style = v);
        if (config.Style == PointerStyle.Minion) changed |= MinionSettings();
        if (ImGui.CollapsingHeader("Simple pointers"))
        {
            changed |= Radio("Light", PointerStyle.Bead, config.Style, v=>config.Style=v);
            ImGui.SameLine(); changed |= Radio("Flat glove", PointerStyle.Glove, config.Style, v=>config.Style=v);
            ImGui.SameLine(); changed |= Radio("Both", PointerStyle.Both, config.Style, v=>config.Style=v);
            changed |= Check("Use the game's cursor texture",config.UseGameGlove,v=>config.UseGameGlove=v);
            changed |= Slider("Light distance",config.BeadDistance,1.5f,3f,v=>config.BeadDistance=v);
            changed |= Slider("Light height",config.BeadHeight,0,2.5f,v=>config.BeadHeight=v);
            changed |= Slider("Light size",config.BeadSize,3,20,v=>config.BeadSize=v);
            changed |= Slider("Flat glove size",config.GloveSize,20,96,v=>config.GloveSize=v);
        }
        ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Trail"))
        {
        ImGui.TextWrapped("A ribbon of light shows the route. Tune its glow, rhythm and placement.");
        foreach (var (label, color) in new[]{("Moonlight",new Vector4(.35f,.87f,1,1)),("Lantern",new Vector4(1,.76f,.3f,1)),("Wisteria",new Vector4(.75f,.55f,1,1))})
        {
            if (ImGui.Button(label)) {config.BeadColor=color;config.OneColorForAll=true;config.TrailGlow=1.3f;config.TrailWidth=2.4f;config.TrailOpacity=.9f;changed=true;}
            ImGui.SameLine();
        }
        ImGui.NewLine();
        changed |= Check("One colour for all targets", config.OneColorForAll, v => config.OneColorForAll = v);
        changed |= Color("Explicit target colour", config.BeadColor, v => config.BeadColor = v);
        if (!config.OneColorForAll)
        {
            changed |= Color("Map flag colour", config.FlagColor, v => config.FlagColor = v);
            changed |= Color("Quest colour", config.QuestColor, v => config.QuestColor = v);
        }

        changed |= Check("Highlight the way (trail)", config.Trail, v => config.Trail = v);
        if (config.Trail)
        {
            if (ImGui.RadioButton("Fairy glow and dust", config.FairyTrail)) { config.FairyTrail = true; changed = true; }
            ImGui.SameLine();
            if (ImGui.RadioButton("Classic line", !config.FairyTrail)) { config.FairyTrail = false; changed = true; }
            ImGui.TextWrapped(config.FairyTrail
                ? "Soft lights drift along the walkable route, occluded by the world and composed behind game UI. Reduced motion keeps a steady glow."
                : "The original line and guiding sparks remain available below.");
            changed |= Radio("On the ground##trail", TrailElevation.Ground, config.TrailElevation, v => config.TrailElevation = v);
            ImGui.SameLine();
            changed |= Radio("Floating overhead##trail", TrailElevation.Overhead, config.TrailElevation, v => config.TrailElevation = v);
            if (config.TrailElevation == TrailElevation.Overhead)
                changed |= Slider("Trail height above you (yalms)", config.TrailHeight, 2.5f, 12f, v => config.TrailHeight = v);
            if (!config.FairyTrail)
            {
                changed |= Check("Glowing line", config.TrailLine, v=>config.TrailLine=v);
                changed |= Check("Guiding sparks", config.TrailBeads, v=>config.TrailBeads=v);
            }
            changed |= Slider("Line width", config.TrailWidth,.5f,6f,v=>config.TrailWidth=v);
            changed |= Slider("Glow", config.TrailGlow,0,2,v=>config.TrailGlow=v);
            changed |= Slider("Opacity", config.TrailOpacity,.1f,1,v=>config.TrailOpacity=v);
            changed |= Slider("Pulse cycle (seconds)", config.TrailPulseSeconds,.6f,8,v=>config.TrailPulseSeconds=v);
            changed |= Check("Reduced motion",config.ReducedMotion,v=>config.ReducedMotion=v);
            var dots = config.TrailDots;
            if (ImGui.SliderInt("Trail beads", ref dots, 2, 40))
            {
                config.TrailDots = dots;
                changed = true;
            }

            changed |= Slider("Trail spacing (yalms)", config.TrailSpacing, 1f, 8f, v => config.TrailSpacing = v);
            var s2 = state();
            ImGui.TextDisabled(s2.Navmesh
                ? "The trail follows vnavmesh's walkable path."
                : "Waiting for a confirmed walking route" + (s2.RouteNote.Length > 0 ? " (vnavmesh: " + s2.RouteNote + ")." : "."));
        }

        ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Interface"))
        {
        changed |= Check("Show route instruction",config.ShowRouteCue,v=>config.ShowRouteCue=v);
        changed |= Check("Show the next step",config.ShowRouteDetail,v=>config.ShowRouteDetail=v);
        changed |= Slider("Instruction horizontal position",config.RouteCueX,0,1,v=>config.RouteCueX=v);
        changed |= Slider("Instruction vertical position",config.RouteCueY,0,1,v=>config.RouteCueY=v);
        ImGui.TextUnformatted("Distance");
        changed |= Radio("Never", DistanceMode.Never, config.Distance, v => config.Distance = v);
        ImGui.SameLine();
        changed |= Radio("On hover", DistanceMode.Hover, config.Distance, v => config.Distance = v);
        ImGui.SameLine();
        changed |= Radio("Always", DistanceMode.Always, config.Distance, v => config.Distance = v);
        changed |= Slider("Arrival radius (yalms)", config.ArriveRadius, 1f, 20f, v => config.ArriveRadius = v);

        ImGui.Separator();
        ImGui.TextUnformatted("Step aside");
        ImGui.TextDisabled("Always hidden in cutscenes, zone changes, group pose and with the game UI hidden.");
        changed |= Check("In combat", config.HideInCombat, v => config.HideInCombat = v);
        changed |= Check("In duties", config.HideInDuty, v => config.HideInDuty = v);

        ImGui.Separator();
        changed |= Check("Follow vnavmesh's walkable path when it is installed", config.UseNavmesh, v => config.UseNavmesh = v);
        ImGui.TextDisabled("vnavmesh: " + navmeshStatus() + ". XivWayfinder never walks your character; automatic Teleport is optional.");

        ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
        if (changed)
        {
            config.Clamp();
            save();
        }
    }

    /// <summary>Where the minion glove floats and how it turns; shown only when it is the chosen pointer.</summary>
    private bool MinionSettings()
    {
        var changed = false;
        ImGui.Indent();
        if (ImGui.Button("Browse and preview models…", new Vector2(280, 34))) OpenModels?.Invoke();
        if (ImGui.Button("Your uploaded model collection…", new Vector2(280, 34))) OpenLocalModels?.Invoke();
        changed |= Check("Guide walks on the ground", config.GuideGrounded, v => config.GuideGrounded = v);
        changed |= Check("Animate the guide",config.GuideAnimate,v=>config.GuideAnimate=v);
        ImGui.TextWrapped(minionStatus());
        ImGui.TextDisabled("Your guide is visible only to you.");
        if (ImGui.CollapsingHeader("Import your own model"))
        {
        if (ImGui.InputText("Local model file (OBJ / GLB / glTF)", ref modelPathDraft, 2048, ImGuiInputTextFlags.EnterReturnsTrue))
            PreviewLocalFile?.Invoke(modelPathDraft);
        if (ImGui.Button("Preview model…")) PreviewLocalFile?.Invoke(modelPathDraft);
        ImGui.SameLine();
        if (ImGui.Button("Use game model")) { config.GuideModelPath = modelPathDraft = ""; changed = true; }
        ImGui.TextWrapped("Preview OBJ, GLB or glTF models with their textures before choosing a guide. GLB and glTF can include skeletal animations and morphs. Keep downloaded model folders intact.");
        }
        if (config.GuideModelPath.Length > 0 && ImportedAnimations?.Invoke() is { Length: > 0 } animations
            && ImGui.BeginCombo("Model animation", config.GuideAnimation.Length == 0 ? "Automatic (idle / walk / run)" : config.GuideAnimation))
        {
            if (ImGui.Selectable("Automatic (idle / walk / run)", config.GuideAnimation.Length == 0)) { config.GuideAnimation=""; changed=true; }
            foreach (var animation in animations)
                if (ImGui.Selectable(animation,config.GuideAnimation==animation)) { config.GuideAnimation=animation; changed=true; }
            ImGui.EndCombo();
        }
        if (config.GuideModelPath.Length > 0) changed |= Check("Keep animation centered on the guide",config.GuideInPlace,v=>config.GuideInPlace=v);
        changed |= Check("Show the bead too", config.MinionWithBead, v => config.MinionWithBead = v);
        changed |= Slider("Lead by (yalms)", config.MinionLeadDistance, 2f, 10f, v => config.MinionLeadDistance = v);
        ImGui.TextWrapped("Walks its own navmesh route, rounds corners and returns when you stray. With a trail it chases the light; without a trail it takes point at least six yalms ahead. Legacy shoulder offsets do not attach it to your character.");
        if (!config.GuideGrounded) changed |= Slider("Float height (yalms)", config.MinionHeight, 0.2f, 12f, v => config.MinionHeight = v);
        changed |= Slider("Guide size", config.MinionSize, 0.01f, 4f, v => config.MinionSize = v);
        var turn = config.MinionTurn;
        if (ImGui.SliderFloat("Turn the model (degrees)", ref turn, -180f, 180f, "%.0f"))
        {
            config.MinionTurn = turn;
            changed = true;
        }

        ImGui.TextDisabled("Adjust the forward direction for your chosen model.");
        changed |= Check("Tilt it up and down slopes", config.MinionTilt, v => config.MinionTilt = v);
        ImGui.Unindent();
        return changed;
    }

    private void DrawJourney(WayfinderState state)
    {
        var journey = config.Journey;
        if (!ImGui.CollapsingHeader("Journey · stops and next actions", ImGuiTreeNodeFlags.DefaultOpen)) return;
        var cue = JourneyCue.From(state.Guide, state.ZoneName, state.Navmesh, state.RouteNote);
        ImGui.TextWrapped(cue.Action);
        ImGui.TextWrapped(cue.Next);
        var changed = false;
        if (ImGui.Button("Add current destination")) changed |= journey.Add(state.Guide.Target);
        if (ImGui.BeginCombo("Add saved place", "Choose a place…"))
        {
            foreach (var place in config.Places)
                if (ImGui.Selectable(place.Name))
                    changed |= journey.Add(new Target(TargetSource.Explicit, place.TerritoryId,
                        place.X, place.Z, place.Y, place.Name));
            ImGui.EndCombo();
        }
        if (journey.Running)
        {
            if (ImGui.Button("Pause journey")) { journey.Running = false; changed = true; }
        }
        else if (ImGui.Button("Start / resume journey") && journey.Resume())
        { config.Enabled = true; changed = true; }
        ImGui.SameLine();
        if (ImGui.Button("Done / skip to next stop")) changed |= journey.Advance();
        ImGui.SameLine();
        if (ImGui.Button("Previous stop")) changed |= journey.Back();
        ImGui.TextWrapped("Stops advance only when you confirm here. Reaching a quest marker does not complete its interaction. Map links and single destinations pause the journey.");
        ImGui.TextUnformatted($"{Math.Min(journey.Index, journey.Stops.Count)} of {journey.Stops.Count} stops advanced");
        for (var i = 0; i < journey.Stops.Count; i++)
        {
            ImGui.PushID(i);
            var label = journey.Stops[i].Label;
            ImGui.TextUnformatted($"{(i < journey.Index ? "✓" : i == journey.Index ? "→" : "·")} {i + 1}. {(label.Length == 0 ? "Destination" : label)}");
            if (i >= journey.Index)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Up")) changed |= journey.Move(i, i - 1);
                ImGui.SameLine();
                if (ImGui.SmallButton("Down")) changed |= journey.Move(i, i + 1);
                ImGui.SameLine();
                ImGui.BeginDisabled(journey.Running && i == journey.Index);
                var remove = ImGui.SmallButton("Remove");
                ImGui.EndDisabled();
                if (remove && journey.Remove(i)) { changed = true; ImGui.PopID(); break; }
            }
            ImGui.PopID();
        }
        if (journey.Stops.Count > 0 && ImGui.Button("Clear itinerary"))
            confirmClearJourney = true;
        if (confirmClearJourney)
        {
            ImGui.TextWrapped("Clear all stops and saved journey progress?");
            if (ImGui.Button("Yes, clear all stops"))
            { config.Journey = new JourneyPlan(); confirmClearJourney = false; changed = true; }
            ImGui.SameLine();
            if (ImGui.Button("Keep itinerary")) confirmClearJourney = false;
        }
        if (changed) save();
        ImGui.Separator();
    }

    private static bool Check(string label, bool value, Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value))
            return false;
        set(value);
        return true;
    }

    private static bool Radio<T>(string label, T option, T current, Action<T> set)
        where T : struct, Enum
    {
        if (!ImGui.RadioButton(label, EqualityComparer<T>.Default.Equals(option, current)))
            return false;
        set(option);
        return true;
    }

    private static bool Slider(string label, float value, float min, float max, Action<float> set)
    {
        if (!ImGui.SliderFloat(label, ref value, min, max, "%.1f"))
            return false;
        set(value);
        return true;
    }

    private static bool Color(string label, Vector4 value, Action<Vector4> set)
    {
        if (!ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.NoInputs))
            return false;
        set(value);
        return true;
    }
}
