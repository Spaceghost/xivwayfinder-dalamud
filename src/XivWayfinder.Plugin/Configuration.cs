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

    /// <summary>Which quest: the next step nearest you of all you have accepted (default), or the tracked one first.</summary>
    public QuestChoice QuestChoice { get; set; } = QuestChoice.Nearest;

    /// <summary>On your Main Scenario quest's steps: its amber colour, a "Main Scenario" label, and an eager glove.</summary>
    public bool MainScenarioStyle { get; set; } = true;

    /// <summary>For a target in another zone, name the nearest aetheryte to it instead of pointing.</summary>
    public bool AetheryteHint { get; set; } = true;

    /// <summary>Opt-in: cast the guide's attuned Teleport destination when idle. Normal gil costs apply.</summary>
    public bool AutoTeleport { get; set; }

    /// <summary>Maximum quoted gil for one automatic Teleport (zero permits only free destinations).</summary>
    public uint AutoTeleportMaxGil { get; set; } = 2000;

    public PointerStyle Style { get; set; } = PointerStyle.Minion;

    /// <summary>The game's own glove cursor when it can be read; off draws the vector glove.</summary>
    public bool UseGameGlove { get; set; } = true;

    /// <summary>How far ahead of the character the bead floats, yalms (1.5–3).</summary>
    public float BeadDistance { get; set; } = 2.2f;

    /// <summary>The bead's height above the character's feet, yalms.</summary>
    public float BeadHeight { get; set; } = 1.1f;

    /// <summary>The bead's core radius in pixels at a normal camera distance.</summary>
    public float BeadSize { get; set; } = 8f;

    public Vector4 BeadColor { get; set; } = Colors.Gold;

    /// <summary>Use <see cref="BeadColor"/> for every source instead of the source-specific colours.</summary>
    public bool OneColorForAll { get; set; }

    public Vector4 FlagColor { get; set; } = Colors.Flag;
    public Vector4 QuestColor { get; set; } = Colors.Quest;

    /// <summary>The glove picture's edge in pixels.</summary>
    public float GloveSize { get; set; } = 44f;

    /// <summary>Let the cursor rush to and hover over a visible destination, independent of the trail.</summary>
    public bool DestinationPointer { get; set; } = true;
    public bool PointerSound { get; set; } = true;
    public float PointerSoundVolume { get; set; } = .35f;

    /// <summary>With the minion glove, also show the bead.</summary>
    public bool MinionWithBead { get; set; } = false;

    /// <summary>How far ahead of the character a beside companion stands, yalms; zero is at the shoulder.</summary>
    public float MinionAhead { get; set; } = 1.6f;

    /// <summary>How far to the character's right (negative: left) a beside companion stands, yalms.</summary>
    public float MinionSide { get; set; } = 0f;

    /// <summary>Its height above the character's feet, yalms.</summary>
    public float MinionHeight { get; set; } = 2.4f;

    /// <summary>The model's size, 1 as the game makes it.</summary>
    public float MinionSize { get; set; } = 1f;
    public Dictionary<uint, GuideModelProfile> ModelProfiles { get; set; } = [];
    public bool GuideAnimate { get; set; } = true;
    public bool GuideGrounded { get; set; }
    public uint GuideModelChara { get; set; }
    public uint GuideNpcId { get; set; }
    public Dictionary<uint, GuideModelProfile> NpcProfiles { get; set; } = [];
    public uint GuideBNpcId { get; set; }
    public Dictionary<uint, GuideModelProfile> BNpcProfiles { get; set; } = [];
    public string GuideAnimation { get; set; } = "";
    public bool GuideInPlace { get; set; } = true;
    public string GuideModelPath { get; set; } = "";
    public List<ImportedGuideProfile> ImportedModels { get; set; } = [];

    /// <summary>Degrees added to its yaw, for when the finger does not point the way in game.</summary>
    public float MinionTurn { get; set; }

    /// <summary>Tilt it up and down slopes (writes the draw object's rotation; not verified in game).</summary>
    public bool MinionTilt { get; set; } = true;

    /// <summary>The minion glove leads the way: it floats ahead along the route instead of beside the bead.</summary>
    public bool MinionLeads { get; set; } = true;

    /// <summary>How far ahead along the route it leads, yalms (2–10).</summary>
    public float MinionLeadDistance { get; set; } = 4f;

    /// <summary>
    /// Map links lead instead of opening the map: a &lt;flag&gt; link or a quest's Show on Map starts following that
    /// place (see <see cref="MapLinks"/>). Hold Shift to open the map as usual.
    /// </summary>
    public bool MapLinks { get; set; } = true;

    /// <summary>Following a map link switches the pointer to the minion glove, to lead the way.</summary>
    public bool MapLinksMinion { get; set; } = true;

    /// <summary>Highlight only a confirmed vnavmesh route. Never falls back to a line through unknown geometry.</summary>
    public bool Trail { get; set; } = true;
    /// <summary>Native scene-depth fairy lights; false retains the classic line and bead controls.</summary>
    public bool FairyTrail { get; set; } = true;
    public TrailElevation TrailElevation { get; set; } = TrailElevation.Overhead;
    public float TrailHeight { get; set; } = 3f;
    public float TrailWidth { get; set; } = 2.2f;
    public float TrailGlow { get; set; } = 1f;
    public float TrailOpacity { get; set; } = .85f;
    public float TrailPulseSeconds { get; set; } = 2.4f;
    public bool TrailLine { get; set; } = true;
    public bool TrailBeads { get; set; } = true;
    public bool ReducedMotion { get; set; }
    public bool ShowRouteCue { get; set; } = true;
    public bool ShowRouteDetail { get; set; } = true;
    public float RouteCueX { get; set; } = 1f;
    public float RouteCueY { get; set; } = .04f;

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

    public List<SavedPlace> Places { get; set; } = [];
    public JourneyPlan Journey { get; set; } = new();

    public SourceToggles Toggles() => new(UseExplicit, UseFlag, UseQuest);

    public MinionLayout MinionPlacement() => new(MinionAhead, MinionSide, MinionHeight, MinionTurn, MinionTilt ? MinionLayout.Default.MaxPitchDegrees : 0f);

    public void StandBeside()
    {
        MinionLeads = false;
        MinionAhead = MinionLayout.BesideDefault.Ahead;
        MinionSide = MinionLayout.BesideDefault.Side;
    }

    /// <summary>Keeps hand-edited or old values inside what the overlay can draw.</summary>
    public void Clamp()
    {
        PointerSoundVolume = float.IsFinite(PointerSoundVolume) ? Math.Clamp(PointerSoundVolume, 0, 1) : .35f;
        AutoTeleportMaxGil = Math.Min(AutoTeleportMaxGil, 100_000);
        GuideModelPath ??= "";
        GuideAnimation ??= "";
        ImportedModels = (ImportedModels ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Path)
                && p.Path.Length <= 2048).DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Take(32)
            .Select(p => p with { Size = Fit(p.Size, GuidePreviewFit.MinimumScale, 4, 1.5f), Turn = Fit(p.Turn, -180, 180, 0),
                Animation = p.Animation ?? "" }).ToList();
        NpcProfiles ??= [];
        BNpcProfiles ??= [];
        ModelProfiles ??= [];
        Places = SavedPlaces.Sanitize(Places);
        Journey ??= new();
        Journey.Sanitize();
        BeadDistance = Fit(BeadDistance, 1.5f, 3f, 2.2f);
        BeadHeight = Fit(BeadHeight, 0f, 2.5f, 1.1f);
        BeadSize = Fit(BeadSize, 3f, 20f, 8f);
        GloveSize = Fit(GloveSize, 20f, 96f, 44f);
        MinionAhead = MinionLayout.CompanionAhead(MinionAhead);
        MinionSide = MinionLayout.CompanionSide(MinionSide);
        MinionHeight = Fit(MinionHeight, 0.2f, 12f, MinionLayout.Default.Height);
        MinionSize = Fit(MinionSize, GuidePreviewFit.MinimumScale, 4f, 1f);
        MinionTurn = Fit(MinionTurn, -180f, 180f, 0f);
        MinionLeadDistance = Fit(MinionLeadDistance, 2f, 10f, 4f);
        if (!Enum.IsDefined(TrailElevation)) TrailElevation = TrailElevation.Overhead;
        TrailWidth = Fit(TrailWidth,.5f,6f,2.2f);
        TrailGlow = Fit(TrailGlow,0,2,1);
        TrailOpacity = Fit(TrailOpacity,.1f,1,.85f);
        TrailPulseSeconds = Fit(TrailPulseSeconds,.6f,8,2.4f);
        RouteCueX = Fit(RouteCueX,0,1,1); RouteCueY = Fit(RouteCueY,0,1,.04f);
        TrailHeight = Fit(TrailHeight, 2.5f, 12f, 3f);
        TrailDots = Math.Clamp(TrailDots, 2, 40);
        TrailSpacing = Fit(TrailSpacing, 1f, 8f, 2.5f);
        ArriveRadius = Fit(ArriveRadius, 1f, 20f, 4f);
        if (!Enum.IsDefined(Mode))
            Mode = SourceMode.Auto;
        if (!Enum.IsDefined(Style))
            Style = PointerStyle.Minion;
        if (!Enum.IsDefined(QuestChoice))
            QuestChoice = QuestChoice.Nearest;
        if (!Enum.IsDefined(Distance))
            Distance = DistanceMode.Hover;
    }

    private static float Fit(float value, float min, float max, float fallback) => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed record GuideModelProfile(float Size, float Turn, bool Grounded, bool Leads = true);
