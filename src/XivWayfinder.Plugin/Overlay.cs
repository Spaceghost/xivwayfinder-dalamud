using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using XivWayfinder.Core;

namespace XivWayfinder.Plugin;

/// <summary>What the overlay draws this frame, from the framework update.</summary>
internal readonly record struct Scene(bool Visible, Guide Guide, Vector3 Player, float Rotation, Vector3? Aim, string TargetZone,
    GuideChasePose? Companion = null, uint TerritoryId = 0, bool CompanionWaitingAside = false);

/// <summary>
/// The pointer, drawn on ImGui's foreground draw list with <c>WorldToScreen</c>: a softly breathing bead floating
/// ahead of the character, a pointing glove near it (or at the screen's edge when the way lies off screen), an
/// optional dotted trail, the distance, a ring on arrival, and a "teleport to" note for another zone. Uses only
/// draw-list calls (no ImGui state is pushed), and never throws: a failure is logged once a minute and the
/// frame is skipped.
/// </summary>
internal sealed class Overlay(IGameGui gameGui, Configuration config, GloveTexture gameGlove, Navmesh navmesh, NativeFairyTrail fairyTrail, IPluginLog log)
{
    private const float GoalLookAhead = 40f;

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double last;
    private double lastError = double.NegativeInfinity;
    private float appear;
    private float glow = 0.3f;
    private bool mirrored;
    private Vector3? warningCorner;
    private double warningStarted;

    // The bead's colour eases between source colours instead of snapping when the selected target changes.
    private Vector4 bead = Colors.Gold;
    private Target? colorTarget;
    public TrailTerrainCheck? TerrainCheck { get; set; }

    // Share the actual eased trail tint, not a second approximation or a rug-specific palette.
    public Vector4 RouteColor(Target? target) => Equals(colorTarget, target) ? bead :
        Colors.For(target, config.BeadColor, config.FlagColor, config.QuestColor,
            config.OneColorForAll, config.MainScenarioStyle);

    public void Draw(in Scene scene, bool modelPreview = false)
    {
        fairyTrail.Clear();
        try
        {
            var terrain = scene.Visible ? TerrainCheck?.GetPreview(scene.TerritoryId, scene.Player) : null;
            DrawCore(scene, modelPreview, terrain is not null);
            if (terrain is not null)
            {
                DrawTrail(ImGui.GetForegroundDrawList(), terrain.Scene, terrain.Scene.Guide.Direction,
                    clock.Elapsed.TotalSeconds, terrain);
                if (gameGui.WorldToScreen(terrain.Focus + Vector3.UnitY * .6f, out var at, out var visible) && visible)
                    ImGui.GetForegroundDrawList().AddText(at + new Vector2(10, -10), 0xffb7e8ff,
                        terrain.Scene.Guide.Target!.Label + " (temporary)");
            }
        }
        catch (Exception ex)
        {
            var now = clock.Elapsed.TotalSeconds;
            if (now - lastError > 60)
            {
                lastError = now;
                log.Error(ex, "XivWayfinder: drawing the pointer failed; skipping this frame");
            }
        }
    }

    private void DrawCore(in Scene scene, bool modelPreview, bool terrainPreview)
    {
        var t = clock.Elapsed.TotalSeconds;
        var dt = (float)Math.Clamp(t - last, 0, 0.1);
        last = t;
        var show = scene.Visible && scene.Guide.Kind != GuideKind.None;
        var beadTarget = Colors.For(
            scene.Guide.Target,
            config.BeadColor,
            config.FlagColor,
            config.QuestColor,
            config.OneColorForAll,
            config.MainScenarioStyle);
        bead = Pulse.Approach(bead, beadTarget, dt, 0.1f);
        colorTarget = scene.Guide.Target;
        appear = Pulse.Approach(appear, show ? 1f : 0f, dt, 0.12f);
        if (!show || appear < 0.01f)
            return;

        var dl = ImGui.GetForegroundDrawList();
        var viewport = ImGui.GetMainViewport();
        var min = viewport.Pos;
        var max = viewport.Pos + viewport.Size;
        DrawTravelCue(dl, scene, min, max);
        switch (scene.Guide.Kind)
        {
            case GuideKind.Walk:
                DrawWalk(dl, scene, t, dt, min, max, modelPreview, terrainPreview);
                break;
            case GuideKind.Arrived:
                if (!terrainPreview) DrawArrived(dl, scene, t);
                break;
            case GuideKind.Teleport or GuideKind.Elsewhere:
                break;
        }
    }

    private void DrawWalk(ImDrawListPtr dl, in Scene scene, double t, float dt, Vector2 min, Vector2 max, bool modelPreview, bool terrainPreview)
    {
        // Never substitute a straight-line pointer while a walkable route is
        // unavailable. The text cue above remains visible with the reason.
        if (!navmesh.Following || navmesh.Path.Length < 2 || scene.Aim is null) return;
        var guide = scene.Guide;
        var dir = guide.Direction;
        if (dir == Vector2.Zero)
            return;
        var p = scene.Player;
        var breath = Pulse.Breath(t, 2.6f);
        var turn = Heading.TurnAway(scene.Rotation, dir);
        glow = Pulse.Approach(glow, Pulse.Glow(turn, breath, 0.28f, 1f), dt, 0.15f);
        var alpha = glow * appear;

        // Which way is "that way" on screen: two projections close to the character, both in front of the camera
        // even when the target is behind it.
        var chest = p + new Vector3(0f, 1.2f, 0f);
        var haveChest = gameGui.WorldToScreen(chest, out var sChest, out _);
        var screenDir = new Vector2(0f, 1f);
        var hasDir = haveChest && gameGui.WorldToScreen(Heading.Ahead(p, dir, 1.5f, 1.2f), out var sAhead, out _) && ScreenEdge.Direction(sChest, sAhead, ref screenDir);

        // Is the way to go on screen? The aim point (a path corner or the target), at most 40 yalms out.
        var goal = scene.Aim ?? guide.Target!.Position(p.Y);
        var reach = MathF.Min(Heading.FlatDistance(p, goal), GoalLookAhead);
        var goalPoint = Heading.Ahead(p, dir, reach, 1.2f);
        var goalInFront = gameGui.WorldToScreen(goalPoint, out var sGoal, out var goalInView);
        if (!hasDir && goalInFront && haveChest)
            hasDir = ScreenEdge.Direction(sChest, sGoal, ref screenDir);

        var tap = Pulse.Tap(t, 1.1f);
        var warning = navmesh.Following
            ? PathFollow.ApproachingCorner(navmesh.Path, navmesh.Corner, p)
            : null;
        if (warning is { } cornerTurn && hasDir)
        {
            var corner = navmesh.Path[navmesh.Corner];
            if (warningCorner != corner)
            {
                warningCorner = corner;
                warningStarted = t;
            }

            // Project the next path leg like the current direction, then ease the glove towards it as the player
            // closes the final three yalms. The bead and trail continue to mark the current leg.
            var turnPoint = Heading.Ahead(p, cornerTurn.Direction, 1.5f, 1.2f);
            var turnScreen = Vector2.Zero;
            if (haveChest && gameGui.WorldToScreen(turnPoint, out var sTurn, out _) && ScreenEdge.Direction(sChest, sTurn, ref turnScreen))
            {
                var leaned = Vector2.Lerp(screenDir, turnScreen, cornerTurn.Blend);
                if (leaned.LengthSquared() > 1e-6f)
                    screenDir = Vector2.Normalize(leaned);
            }

            // Two quick taps for one ordinary 1.1-second tap period, then return to the normal rhythm.
            if (t - warningStarted < 1.1)
                tap = Pulse.Tap(t - warningStarted, 0.55f);
        }
        else
        {
            warningCorner = null;
        }

        var gloveMargin = config.GloveSize * 0.8f;
        var onScreen = goalInFront && goalInView && ScreenEdge.Inside(sGoal, min + new Vector2(gloveMargin), max - new Vector2(gloveMargin));

        if (config.Trail && !terrainPreview)
            DrawTrail(dl, scene, dir, t);

        // A temporary model preview replaces the saved simple pointer for
        // this frame only. Keep the route and travel cue, and never rewrite
        // the user's saved style just to hide its cursor and paired bead.
        if (modelPreview) return;

        Vector2? hoverAt = null;
        var beadScale = 1f;
        // Model mode has no legacy cursor fallback, even if its model is loading.
        var minionStyle = config.Style == PointerStyle.Minion;
        var paired = config.Style == PointerStyle.Both;
        if (config.Style is PointerStyle.Bead or PointerStyle.Both || (minionStyle && config.MinionWithBead))
        {
            var bead = Heading.Ahead(p, dir, config.BeadDistance, config.BeadHeight + Pulse.Bob(t, 2.2f, 0.12f));
            if (gameGui.WorldToScreen(bead, out var sBead, out var beadInView) && beadInView)
            {
                if (gameGui.WorldToScreen(bead + new Vector3(0f, 0.3f, 0f), out var sUp, out _))
                    beadScale = Math.Clamp(Vector2.Distance(sUp, sBead) / 0.3f / 60f, 0.55f, 1.6f);
                DrawBead(dl, sBead, config.BeadSize * beadScale * (0.92f + 0.16f * breath), alpha);
                hoverAt = sBead;
            }
        }

        // Only explicitly selected simple-pointer styles draw a flat glove.
        if (config.Style is PointerStyle.Glove or PointerStyle.Both)
        {
            var size = config.GloveSize;
            var model = UseGameGlove(out _, out _, out _) ? Glove.GameSprite : VectorGlove.Model;
            Vector2 anchor;
            float gloveAlpha;
            var angle = ScreenEdge.Angle(screenDir);
            var above = paired ? 2.1f : 1.6f;
            if (onScreen && gameGui.WorldToScreen(Heading.Ahead(p, dir, paired ? 0.8f : 1.4f, above), out var sNear, out var nearInView) && nearInView)
            {
                anchor = sNear;
                gloveAlpha = appear * (0.55f + 0.45f * glow);
            }
            else
            {
                // off screen: on the view's edge where the direction leaves it, fingertip on the margin
                var origin = haveChest ? sChest : (min + max) * 0.5f;
                var edge = ScreenEdge.RayToEdge(origin, screenDir, min + new Vector2(gloveMargin), max - new Vector2(gloveMargin));
                anchor = edge - screenDir * (model.Reach * size * 0.6f);
                gloveAlpha = appear;
            }

            anchor += screenDir * (tap * size * 0.12f);
            mirrored = Glove.Mirror(angle, mirrored);
            DrawGlove(dl, anchor, size, angle, gloveAlpha);
            hoverAt ??= anchor;
        }

        if (hoverAt is { } at && ShowDistance(at))
        {
            var text = string.Create(CultureInfo.InvariantCulture, $"{guide.Distance:0} y");
            Label(dl, at + new Vector2(0f, 16f * beadScale + 6f), text, appear);
        }

        if (hoverAt is { } ms && config.MainScenarioStyle && guide.Target is { MainScenario: true })
            Label(dl, ms - new Vector2(0f, 16f * beadScale + 10f), "Main Scenario", 0.85f * appear);
    }

    private bool ShowDistance(Vector2 at) => config.Distance switch
    {
        DistanceMode.Always => true,
        DistanceMode.Hover => Vector2.Distance(ImGui.GetMousePos(), at) < 40f,
        _ => false,
    };

    private void DrawBead(ImDrawListPtr dl, Vector2 c, float r, float alpha)
    {
        var colour = bead;
        for (var i = 4; i >= 1; i--)
            dl.AddCircleFilled(c, r * (1f + i * 0.55f), Colors.Pack(colour, alpha * 0.07f * (5 - i)), 32);
        dl.AddCircleFilled(c, r, Colors.Pack(Colors.Lighten(colour, 0.15f), 0.35f + 0.65f * alpha), 32);
        dl.AddCircle(c, r, Colors.Pack(Colors.Lighten(colour, 0.5f), 0.8f * alpha), 32, 1.5f);
        dl.AddCircleFilled(c + new Vector2(-0.3f * r, -0.35f * r), r * 0.32f, Colors.Pack(Vector4.One, 0.55f * alpha), 16);
    }

    private bool UseGameGlove(out Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? wrap, out Vector2 uvMin, out Vector2 uvMax)
    {
        wrap = null;
        uvMin = Vector2.Zero;
        uvMax = Vector2.One;
        return config.UseGameGlove && gameGlove.TryGet(out wrap, out uvMin, out uvMax) && wrap is not null;
    }

    public void DrawLoadingPointer(MinionPose pose)
    {
        if (!gameGui.WorldToScreen(pose.Position, out var at, out var visible) || !visible) return;
        var direction = Vector3.Transform(Vector3.UnitZ, MinionGlove.Orientation(pose.Yaw, pose.Pitch));
        var angle = -MathF.PI / 2;
        if (gameGui.WorldToScreen(pose.Position + direction, out var tip, out _) && Vector2.DistanceSquared(at, tip) > .01f)
            angle = MathF.Atan2(tip.Y - at.Y, tip.X - at.X);
        DrawGlove(ImGui.GetForegroundDrawList(), at, Math.Clamp(config.GloveSize, 32, 64), angle, 1);
    }

    private void DrawGlove(ImDrawListPtr dl, Vector2 anchor, float size, float angle, float alpha)
    {
        dl.AddCircleFilled(anchor, size * 0.42f, Colors.Pack(bead, 0.18f * alpha), 24);
        Span<Vector2> quad = stackalloc Vector2[4];
        if (UseGameGlove(out var wrap, out var uv0, out var uv1))
        {
            Glove.Quad(Glove.GameSprite, anchor, size, angle, mirrored, quad);
            dl.AddImageQuad(wrap!.Handle, quad[0], quad[1], quad[2], quad[3],
                uv0, new Vector2(uv1.X, uv0.Y), uv1, new Vector2(uv0.X, uv1.Y), Colors.Pack(Vector4.One, alpha));
            return;
        }

        // the drawn glove's units are palm-to-fingertip lengths; half the picture size matches the sprite's hand
        var unit = size * 0.5f;
        var axis = VectorGlove.Model.AxisAngle;
        Span<Vector2> points = stackalloc Vector2[64];
        var fill = Colors.Pack(Colors.Ivory, alpha);
        var ink = Colors.Pack(Colors.Ink, 0.9f * alpha);
        foreach (var shape in VectorGlove.Shapes)
        {
            var n = Math.Min(shape.Length, points.Length);
            for (var i = 0; i < n; i++)
                points[i] = Glove.Place(shape[i], axis, anchor, unit, angle, mirrored);
            dl.AddConvexPolyFilled(ref points[0], n, fill);
            dl.AddPolyline(ref points[0], n, ink, ImDrawFlags.Closed, 1.8f);
        }
    }

    private readonly TrailGround trailGround = new(message => log.Information(message));
    private const int MaxTrail = 48;
    private const int MaxLine = 128;

    /// <summary>
    /// The native scene-depth trail along vnavmesh's confirmed path: beads fixed to the route every
    /// <see cref="Configuration.TrailSpacing"/> yalms, the next stretch ahead of the character joined by a soft line,
    /// brightest near the character and fading along, with a shimmer travelling outward; beads walked past drop away
    /// behind. Missing route or terrain data hides the unverified geometry.
    /// </summary>
    private void DrawTrail(ImDrawListPtr dl, in Scene scene, Vector2 dir, double t, TerrainTrailPreview? terrain = null)
    {
        if (!config.FairyTrail && !config.TrailLine && !config.TrailBeads) return;
        var path = terrain?.Path ?? (navmesh.Following ? navmesh.Path : []);
        var progressArc = terrain?.ProgressArc ?? navmesh.ProgressArc;
        var trailAppearance = terrain is null ? appear : 1f;
        var trailColor = terrain is null ? bead : Colors.Gold;
        // Every style follows the confirmed route and uses the native scene-depth pass.
        if (path.Length < 2) return;
        var p = scene.Player;
        trailGround.Begin(scene.TerritoryId, config.TrailElevation == TrailElevation.Overhead);
        var length = config.TrailSpacing * config.TrailDots + 2f;
        if (config.FairyTrail)
        {
            if (terrain is null && config.Style == PointerStyle.Minion && scene.Companion is null && !scene.CompanionWaitingAside) return;
            Span<FairyMote> motes = stackalloc FairyMote[FairyTrail.MaxMotes];
            var chase = GuideChaseLayout.Create(config.Trail, config.MinionLeadDistance,
                config.MainScenarioStyle && scene.Guide.Target?.MainScenario == true);
            var lightStart = terrain is not null ? .12f : Math.Max(.12f, (scene.Companion?.GuideArc - navmesh.ProgressArc
                ?? chase.GuideLead) + GuideChaseLayout.LightGap);
            var count = FairyTrail.Sample(path, progressArc, length, t,
                config.TrailPulseSeconds, config.ReducedMotion, motes, lightStart);
            Span<FairyTrailParticle> particles = stackalloc FairyTrailParticle[FairyTrail.MaxMotes];
            var placed = 0;
            for (var i = 0; i < count; i++)
            {
                var mote = motes[i];
                if (!TrailPosition(mote.GroundPosition, p, t, out var at, out var normal)) continue;
                // Ground lights remain pinned to their measured plane. Only
                // the overhead style adds the small animated vertical wisp.
                if (normal == Vector3.Zero) at += Vector3.UnitY * mote.Lift;
                particles[placed++] = new(at, mote.Radius * (1 + .5f * config.TrailGlow),
                    mote.Opacity * trailAppearance * config.TrailOpacity, normal);
            }
            fairyTrail.Queue(particles[..placed], trailColor, territory: scene.TerritoryId, time: t);
            return;
        }

        Span<TrailPoint> beads = stackalloc TrailPoint[MaxTrail];
        var arc = progressArc;
        var n = config.TrailBeads ? PathTrail.Sample(path, arc, config.TrailSpacing, .8f, length, beads) : 0;
        // Reserve collision samples for guiding dots before the denser line.
        for (var b = 0; b < n; b++) TrailPosition(beads[b].Position, p, t, out _, out _);
        Span<TrailPoint> line = stackalloc TrailPoint[MaxLine];
        Span<NativeTrailSegment> segments = stackalloc NativeTrailSegment[MaxLine - 1];
        var segmentCount = 0;
        var step = config.TrailElevation == TrailElevation.Ground ? .25f : 1f;
        var m = config.TrailLine ? PathTrail.Sample(path, arc, step, .8f, Math.Min(length, MaxLine * step), line) : 0;
        var previousOk = false;
        var previous = Vector3.Zero;
        var previousNormal = Vector3.Zero;
        var previousAhead = 0f;
        for (var i = 0; i < m; i++)
        {
            var ok = TrailPosition(line[i].Position, p, t, out var world, out var normal);
            if (ok && previousOk && (config.TrailElevation == TrailElevation.Ground || trailGround.CanConnect(previous, world, t)))
            {
                var fade = 1 - .75f * Math.Clamp((previousAhead + line[i].Ahead) * .5f / length, 0, 1);
                var wave = config.ReducedMotion ? .65f : Pulse.TrailAt(line[i].Ahead / length, t, config.TrailPulseSeconds);
                var strength = config.TrailOpacity * fade * trailAppearance * (.4f + .6f * wave);
                segments[segmentCount++] = new(previous, world, previousNormal, normal,
                    config.TrailWidth, strength, config.TrailGlow, wave);
            }
            previousOk = ok;
            previous = world;
            previousNormal = normal;
            previousAhead = line[i].Ahead;
        }

        Span<FairyTrailParticle> dots = stackalloc FairyTrailParticle[MaxTrail * 2];
        var dotCount = 0;
        for (var i = 0; i < n; i++)
        {
            if (!TrailPosition(beads[i].Position, p, t, out var at, out var normal)) continue;
            var near = Pulse.SmoothStep(.8f, 2, beads[i].Ahead);
            var strength = (config.ReducedMotion ? .65f : Pulse.TrailAt(beads[i].Ahead / length, t, config.TrailPulseSeconds))
                * near * trailAppearance * config.TrailOpacity;
            dots[dotCount++] = new(at, NativeFairyTrail.MaximumRadius, .08f * strength * config.TrailGlow,
                normal, 6 + 3 * strength);
            dots[dotCount++] = new(at, NativeFairyTrail.MaximumRadius, .9f * strength,
                normal, 2.2f + 1.6f * strength);
        }
        fairyTrail.Queue(dots[..dotCount], trailColor, segments[..segmentCount], ImGui.GetMainViewport().Size, scene.TerritoryId, t);
    }

    private bool TrailPosition(Vector3 point, Vector3 player, double now, out Vector3 at, out Vector3 normal)
    {
        if (config.TrailElevation == TrailElevation.Ground) return trailGround.TryPlace(point, now, out at, out normal);
        normal = Vector3.Zero;
        return trailGround.TryPlaceOverhead(point, player, config.TrailHeight, now, out at);
    }

    private Target? arrivalTarget;
    private Vector3 arrivalAnchor;

    private void DrawArrived(ImDrawListPtr dl, in Scene scene, double t)
    {
        var target = scene.Guide.Target!;
        if (!Equals(arrivalTarget, target) || (target.Y is null && Math.Abs(scene.Player.Y - arrivalAnchor.Y) > 1.5f))
        {
            arrivalTarget = target;
            arrivalAnchor = target.Position(scene.Player.Y);
        }
        trailGround.Begin(scene.TerritoryId);
        if (!trailGround.TryPlace(arrivalAnchor, t, out var center, out var centerNormal)) return;
        const int n = 32;
        Span<Vector3> ring = stackalloc Vector3[n];
        Span<Vector3> normals = stackalloc Vector3[n];
        Span<bool> ok = stackalloc bool[n];
        // Pulse brightness on fixed ground anchors. Breathing the radius would
        // invalidate every exact floor-cache key on every frame.
        for (var i = 0; i < n; i++)
        {
            var (s, c) = MathF.SinCos(i * Heading.Tau / n);
            ok[i] = trailGround.TryPlace(arrivalAnchor + new Vector3(c, 0, s), t, out ring[i], out normals[i]);
        }
        Span<NativeTrailSegment> segments = stackalloc NativeTrailSegment[n];
        var count = 0;
        var breath = config.ReducedMotion ? .65f : Pulse.Breath(t, 2.6f);
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            if (ok[i] && ok[j])
                segments[count++] = new(ring[i], ring[j], normals[i], normals[j], 2.5f,
                    (.65f + .2f * breath) * appear, config.TrailGlow, breath);
        }
        Span<FairyTrailParticle> marker = stackalloc FairyTrailParticle[1];
        marker[0] = new(center, .12f, appear, centerNormal);
        fairyTrail.Queue(marker, bead, segments[..count], ImGui.GetMainViewport().Size, scene.TerritoryId, t);

        var labelAt = center + new Vector3(0, .9f + (config.ReducedMotion ? 0 : Pulse.Bob(t, 2.2f, .1f)), 0);
        if (gameGui.WorldToScreen(labelAt, out var top, out var inView) && inView)
            Label(dl, top + new Vector2(0, 18), "here", appear);
    }

    private void DrawTravelCue(ImDrawListPtr dl, in Scene scene, Vector2 min, Vector2 max)
    {
        if (!config.ShowRouteCue) return;
        var guide = scene.Guide;
        var cue = JourneyCue.From(guide, scene.TargetZone, navmesh.Following,
            config.UseNavmesh ? navmesh.Status : "pathfinding is disabled");
        var hint = cue.Action;
        // A remote objective has no honest world-space heading. Keep its travel instruction near the
        // screen edge instead of putting an opaque, pointer-shaped window over the character.
        var textSize = ImGui.CalcTextSize(hint);
        var width = MathF.Max(textSize.X, ImGui.CalcTextSize(cue.Next).X);
        var at = min + new Vector2(24 + MathF.Max(0,max.X-min.X-width-54)*config.RouteCueX, 24 + MathF.Max(0,max.Y-min.Y-90)*config.RouteCueY);
        var shadow = Colors.Pack(Colors.Ink, 0.9f * appear);
        dl.AddText(at + new Vector2(1f, 1f), shadow, hint);
        dl.AddText(at, Colors.Pack(Colors.Ivory, appear), hint);
        var nextAt = at + new Vector2(0, textSize.Y + 4);
        if (config.ShowRouteDetail)
        {
            dl.AddText(nextAt + Vector2.One, shadow, cue.Next);
            dl.AddText(nextAt, Colors.Pack(Colors.Ivory, 0.75f * appear), cue.Next);
        }
        dl.AddCircleFilled(at + new Vector2(-9f, textSize.Y * 0.5f), 3.5f, Colors.Pack(bead, appear), 16);
    }

    private static void Label(ImDrawListPtr dl, Vector2 centre, string text, float alpha)
    {
        var size = ImGui.CalcTextSize(text);
        var at = centre - new Vector2(size.X * 0.5f, 0f);
        dl.AddText(at + new Vector2(1f, 1f), Colors.Pack(Colors.Ink, 0.8f * alpha), text);
        dl.AddText(at, Colors.Pack(Colors.Ivory, alpha), text);
    }
}
