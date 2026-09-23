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
    private readonly Action test;
    private readonly Action clear;

    public SettingsWindow(Configuration config, Action save, Func<WayfinderState> state, Func<string> gloveStatus, Func<string> minionStatus, Func<string> navmeshStatus, Action test, Action clear)
        : base("XivWayfinder###WayfinderSettings")
    {
        this.config = config;
        this.save = save;
        this.state = state;
        this.gloveStatus = gloveStatus;
        this.minionStatus = minionStatus;
        this.navmeshStatus = navmeshStatus;
        this.test = test;
        this.clear = clear;
        Size = new Vector2(440, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var changed = false;
        var s = state();
        ImGui.TextWrapped(WayfinderIpc.StatusLine(s));
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
        changed |= Check("The tracked quest's next step", config.UseQuest, v => config.UseQuest = v);
        changed |= Check("In another zone, name the nearest aetheryte", config.AetheryteHint, v => config.AetheryteHint = v);
        changed |= Check("Forget a /wayfinder target once reached", config.ClearOnArrival, v => config.ClearOnArrival = v);

        ImGui.Separator();
        ImGui.TextUnformatted("Pointer");
        changed |= Radio("Bead", PointerStyle.Bead, config.Style, v => config.Style = v);
        ImGui.SameLine();
        changed |= Radio("Glove", PointerStyle.Glove, config.Style, v => config.Style = v);
        ImGui.SameLine();
        changed |= Radio("Both", PointerStyle.Both, config.Style, v => config.Style = v);
        changed |= Radio("Minion glove (Wind-up Cursor model)", PointerStyle.Minion, config.Style, v => config.Style = v);
        changed |= Check("Use the game's own glove cursor", config.UseGameGlove, v => config.UseGameGlove = v);
        ImGui.TextDisabled(gloveStatus());
        if (config.Style == PointerStyle.Minion)
            changed |= MinionSettings();
        changed |= Slider("Bead distance (yalms)", config.BeadDistance, 1.5f, 3f, v => config.BeadDistance = v);
        changed |= Slider("Bead height (yalms)", config.BeadHeight, 0f, 2.5f, v => config.BeadHeight = v);
        changed |= Slider("Bead size", config.BeadSize, 3f, 20f, v => config.BeadSize = v);
        changed |= Slider("Glove size", config.GloveSize, 20f, 96f, v => config.GloveSize = v);
        var colour = config.BeadColor;
        if (ImGui.ColorEdit4("Glow colour", ref colour, ImGuiColorEditFlags.NoInputs))
        {
            config.BeadColor = colour;
            changed = true;
        }

        changed |= Check("Highlight the way (trail)", config.Trail, v => config.Trail = v);
        if (config.Trail)
        {
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
                : "The trail is a straight line, not the walkable way" + (s2.RouteNote.Length > 0 ? " (vnavmesh: " + s2.RouteNote + ")." : "."));
        }

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
        ImGui.TextDisabled("vnavmesh: " + navmeshStatus() + ". XivWayfinder only points; it never moves your character.");

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
        ImGui.TextWrapped(minionStatus());
        ImGui.TextDisabled("Only you see it: a client-side model, never a summoned minion, nothing sent to the server.");
        changed |= Check("Show the bead too", config.MinionWithBead, v => config.MinionWithBead = v);
        changed |= Slider("Glove ahead (yalms)", config.MinionAhead, 0.8f, 3f, v => config.MinionAhead = v);
        changed |= Slider("Glove to the side (yalms)", config.MinionSide, -2f, 2f, v => config.MinionSide = v);
        changed |= Slider("Glove height (yalms)", config.MinionHeight, 0f, 2.5f, v => config.MinionHeight = v);
        changed |= Slider("Glove model size", config.MinionSize, 0.25f, 3f, v => config.MinionSize = v);
        var turn = config.MinionTurn;
        if (ImGui.SliderFloat("Turn the model (degrees)", ref turn, -180f, 180f, "%.0f"))
        {
            config.MinionTurn = turn;
            changed = true;
        }

        ImGui.TextDisabled("If the finger does not point the way, turn it here.");
        changed |= Check("Tilt it up and down slopes", config.MinionTilt, v => config.MinionTilt = v);
        ImGui.Unindent();
        return changed;
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
}
