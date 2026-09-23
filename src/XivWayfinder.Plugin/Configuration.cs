using System.Numerics;
using Dalamud.Configuration;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Draw anything at all. /wayfinder on|off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Follow every source by priority (explicit, flag, quest), or only one.</summary>
    public SourceMode Mode { get; set; } = SourceMode.Auto;

    public bool UseExplicit { get; set; } = true;
    public bool UseFlag { get; set; } = true;
    public bool UseQuest { get; set; } = true;

    /// <summary>For a target in another zone, name the nearest aetheryte to it instead of pointing.</summary>
    public bool AetheryteHint { get; set; } = true;

    public PointerStyle Style { get; set; } = PointerStyle.Both;

    /// <summary>The game's own glove cursor when it can be read; off draws the vector glove.</summary>
    public bool UseGameGlove { get; set; } = true;

    /// <summary>How far ahead of the character the bead floats, yalms (1.5–3).</summary>
    public float BeadDistance { get; set; } = 2.2f;

    /// <summary>The bead's height above the character's feet, yalms.</summary>
    public float BeadHeight { get; set; } = 1.1f;

    /// <summary>The bead's core radius in pixels at a normal camera distance.</summary>
    public float BeadSize { get; set; } = 8f;

    public Vector4 BeadColor { get; set; } = Colors.Gold;

    /// <summary>The glove picture's edge in pixels.</summary>
    public float GloveSize { get; set; } = 44f;

    /// <summary>With the minion glove, also show the bead.</summary>
    public bool MinionWithBead { get; set; } = true;

    /// <summary>How far ahead of the character the minion glove floats, yalms.</summary>
    public float MinionAhead { get; set; } = MinionLayout.Default.Ahead;

    /// <summary>How far to the right of the way (negative: left) it floats, yalms.</summary>
    public float MinionSide { get; set; } = MinionLayout.Default.Side;

    /// <summary>Its height above the character's feet, yalms.</summary>
    public float MinionHeight { get; set; } = MinionLayout.Default.Height;

    /// <summary>The model's size, 1 as the game makes it.</summary>
    public float MinionSize { get; set; } = 1f;

    /// <summary>Degrees added to its yaw, for when the finger does not point the way in game.</summary>
    public float MinionTurn { get; set; }

    /// <summary>Tilt it up and down slopes (writes the draw object's rotation; not verified in game).</summary>
    public bool MinionTilt { get; set; } = true;

    /// <summary>Highlight the way: along vnavmesh's path when there is one, else a straight dotted line.</summary>
    public bool Trail { get; set; } = true;

    /// <summary>Trail beads, spaced <see cref="TrailSpacing"/> yalms apart.</summary>
    public int TrailDots { get; set; } = 10;

    public float TrailSpacing { get; set; } = 2.5f;

    public DistanceMode Distance { get; set; } = DistanceMode.Hover;

    public bool HideInCombat { get; set; } = true;
    public bool HideInDuty { get; set; } = true;

    /// <summary>Point along vnavmesh's walkable path when that plugin is installed.</summary>
    public bool UseNavmesh { get; set; } = true;

    /// <summary>Within this many yalms the target counts as reached.</summary>
    public float ArriveRadius { get; set; } = 4f;

    /// <summary>Forget a /wayfinder or IPC target once it has been reached.</summary>
    public bool ClearOnArrival { get; set; } = true;

    public SourceToggles Toggles() => new(UseExplicit, UseFlag, UseQuest);

    public MinionLayout MinionPlacement() => new(MinionAhead, MinionSide, MinionHeight, MinionTurn, MinionTilt ? MinionLayout.Default.MaxPitchDegrees : 0f);

    /// <summary>Keeps hand-edited or old values inside what the overlay can draw.</summary>
    public void Clamp()
    {
        BeadDistance = Fit(BeadDistance, 1.5f, 3f, 2.2f);
        BeadHeight = Fit(BeadHeight, 0f, 2.5f, 1.1f);
        BeadSize = Fit(BeadSize, 3f, 20f, 8f);
        GloveSize = Fit(GloveSize, 20f, 96f, 44f);
        MinionAhead = Fit(MinionAhead, 0.8f, 3f, MinionLayout.Default.Ahead);
        MinionSide = Fit(MinionSide, -2f, 2f, MinionLayout.Default.Side);
        MinionHeight = Fit(MinionHeight, 0f, 2.5f, MinionLayout.Default.Height);
        MinionSize = Fit(MinionSize, 0.25f, 3f, 1f);
        MinionTurn = Fit(MinionTurn, -180f, 180f, 0f);
        TrailDots = Math.Clamp(TrailDots, 2, 40);
        TrailSpacing = Fit(TrailSpacing, 1f, 8f, 2.5f);
        ArriveRadius = Fit(ArriveRadius, 1f, 20f, 4f);
        if (!Enum.IsDefined(Mode))
            Mode = SourceMode.Auto;
        if (!Enum.IsDefined(Style))
            Style = PointerStyle.Both;
        if (!Enum.IsDefined(Distance))
            Distance = DistanceMode.Hover;
    }

    private static float Fit(float value, float min, float max, float fallback) => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
