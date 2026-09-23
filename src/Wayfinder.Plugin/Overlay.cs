using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Wayfinder.Core;

namespace Wayfinder.Plugin;

/// <summary>What the overlay draws this frame, from the framework update.</summary>
internal readonly record struct Scene(bool Visible, Guide Guide, Vector3 Player, float Rotation, Vector3? Aim, string TargetZone);

/// <summary>
/// The pointer, drawn on ImGui's foreground draw list with <c>WorldToScreen</c>: a softly breathing bead floating
/// ahead of the character, a pointing glove near it (or at the screen's edge when the way lies off screen), an
/// optional dotted trail, the distance, a ring on arrival, and a "teleport to" note for another zone. Uses only
/// draw-list calls (no ImGui state is pushed), and never throws: a failure is logged once a minute and the
/// frame is skipped.
/// </summary>
internal sealed class Overlay(IGameGui gameGui, Configuration config, GloveTexture gameGlove, Navmesh navmesh, IPluginLog log)
{
    private const float GoalLookAhead = 40f;

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double last;
    private double lastError = double.NegativeInfinity;
    private float appear;
    private float glow = 0.3f;
    private bool mirrored;

    public void Draw(in Scene scene)
    {
        try
        {
            DrawCore(scene);
        }
        catch (Exception ex)
        {
            var now = clock.Elapsed.TotalSeconds;
            if (now - lastError > 60)
            {
                lastError = now;
                log.Error(ex, "Wayfinder: drawing the pointer failed; skipping this frame");
            }
        }
    }

    private void DrawCore(in Scene scene)
    {
        var t = clock.Elapsed.TotalSeconds;
        var dt = (float)Math.Clamp(t - last, 0, 0.1);
        last = t;
        var show = scene.Visible && scene.Guide.Kind != GuideKind.None;
        appear = Pulse.Approach(appear, show ? 1f : 0f, dt, 0.12f);
        if (!show || appear < 0.01f)
            return;

        var dl = ImGui.GetForegroundDrawList();
        var viewport = ImGui.GetMainViewport();
        var min = viewport.Pos;
        var max = viewport.Pos + viewport.Size;
        switch (scene.Guide.Kind)
        {
            case GuideKind.Walk:
                DrawWalk(dl, scene, t, dt, min, max);
                break;
            case GuideKind.Arrived:
                DrawArrived(dl, scene, t);
                break;
            case GuideKind.Teleport or GuideKind.Elsewhere:
                DrawNote(dl, scene, t, min, max);
                break;
        }
    }

    private void DrawWalk(ImDrawListPtr dl, in Scene scene, double t, float dt, Vector2 min, Vector2 max)
    {
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
        var gloveMargin = config.GloveSize * 0.8f;
        var onScreen = goalInFront && goalInView && ScreenEdge.Inside(sGoal, min + new Vector2(gloveMargin), max - new Vector2(gloveMargin));

        if (config.Trail)
            DrawTrail(dl, scene, dir, t);

        Vector2? hoverAt = null;
        var beadScale = 1f;
        if (config.Style is PointerStyle.Bead or PointerStyle.Both)
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

        if (config.Style is PointerStyle.Glove or PointerStyle.Both)
        {
            var size = config.GloveSize;
            var model = UseGameGlove(out _, out _, out _) ? Glove.GameSprite : VectorGlove.Model;
            Vector2 anchor;
            float gloveAlpha;
            var angle = ScreenEdge.Angle(screenDir);
            var above = config.Style == PointerStyle.Both ? 2.1f : 1.6f;
            if (onScreen && gameGui.WorldToScreen(Heading.Ahead(p, dir, config.Style == PointerStyle.Both ? 0.8f : 1.4f, above), out var sNear, out var nearInView) && nearInView)
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

            anchor += screenDir * (Pulse.Tap(t, 1.1f) * size * 0.12f);
            mirrored = Glove.Mirror(angle, mirrored);
            DrawGlove(dl, anchor, size, angle, gloveAlpha);
            hoverAt ??= anchor;
        }

        if (hoverAt is { } at && ShowDistance(at))
        {
            var text = string.Create(CultureInfo.InvariantCulture, $"{guide.Distance:0} y");
            Label(dl, at + new Vector2(0f, 16f * beadScale + 6f), text, appear);
        }
    }

    private bool ShowDistance(Vector2 at) => config.Distance switch
    {
        DistanceMode.Always => true,
        DistanceMode.Hover => Vector2.Distance(ImGui.GetMousePos(), at) < 40f,
        _ => false,
    };

    private void DrawBead(ImDrawListPtr dl, Vector2 c, float r, float alpha)
    {
        var colour = config.BeadColor;
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

    private void DrawGlove(ImDrawListPtr dl, Vector2 anchor, float size, float angle, float alpha)
    {
        dl.AddCircleFilled(anchor, size * 0.42f, Colors.Pack(config.BeadColor, 0.18f * alpha), 24);
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

    private void DrawTrail(ImDrawListPtr dl, in Scene scene, Vector2 dir, double t)
    {
        var p = scene.Player;
        var count = config.TrailDots;
        var spacing = config.TrailSpacing;
        var corners = navmesh.Path;
        var corner = navmesh.Corner;
        var total = scene.Guide.Distance;
        for (var i = 0; i < count; i++)
        {
            var along = 2f + i * spacing;
            if (along >= total)
                break;
            Vector3 at;
            if (corner >= 0 && corner < corners.Length)
                at = AlongPath(p, corners, corner, along);
            else
                at = Heading.Ahead(p, dir, along, 0f);
            at.Y += 0.25f;
            if (!gameGui.WorldToScreen(at, out var s, out var inView) || !inView)
                continue;
            var b = Pulse.Trail(i, count, t, 1.8f) * appear;
            dl.AddCircleFilled(s, 2.2f + 1.6f * b, Colors.Pack(config.BeadColor, 0.9f * b), 12);
        }
    }

    /// <summary>The point <paramref name="along"/> yalms from the player along the path's remaining corners.</summary>
    private static Vector3 AlongPath(Vector3 player, ReadOnlySpan<Vector3> corners, int from, float along)
    {
        var a = player;
        for (var i = from; i < corners.Length; i++)
        {
            var b = corners[i];
            var leg = Heading.FlatDistance(a, b);
            if (along <= leg && leg > 1e-4f)
                return Vector3.Lerp(a, b, along / leg);
            along -= leg;
            a = b;
        }

        return a;
    }

    private void DrawArrived(ImDrawListPtr dl, in Scene scene, double t)
    {
        var goal = scene.Guide.Target!.Position(scene.Player.Y);
        var radius = 1f + 0.15f * Pulse.Breath(t, 2.6f);
        const int n = 32;
        Span<Vector2> ring = stackalloc Vector2[n];
        Span<bool> ok = stackalloc bool[n];
        for (var i = 0; i < n; i++)
        {
            var (s, c) = MathF.SinCos(i * Heading.Tau / n);
            ok[i] = gameGui.WorldToScreen(goal + new Vector3(c * radius, 0.05f, s * radius), out ring[i], out _);
        }

        var colour = Colors.Pack(config.BeadColor, 0.85f * appear);
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            if (ok[i] && ok[j])
                dl.AddLine(ring[i], ring[j], colour, 2.5f);
        }

        if (gameGui.WorldToScreen(goal + new Vector3(0f, 0.9f + Pulse.Bob(t, 2.2f, 0.1f), 0f), out var top, out var inView) && inView)
        {
            DrawBead(dl, top, config.BeadSize * 0.8f, appear);
            Label(dl, top + new Vector2(0f, 18f), "here", appear);
        }
    }

    private void DrawNote(ImDrawListPtr dl, in Scene scene, double t, Vector2 min, Vector2 max)
    {
        var guide = scene.Guide;
        var target = guide.Target!;
        var line1 = guide.Kind == GuideKind.Teleport
            ? $"Teleport to {guide.Aetheryte!.Name}" + (guide.Aetheryte.Unlocked ? "" : " (not attuned)")
            : $"In {scene.TargetZone}";
        var line2 = (target.Label.Length > 0 ? target.Label + " · " : "") + scene.TargetZone;
        var at = gameGui.WorldToScreen(scene.Player + new Vector3(0f, 2.7f, 0f), out var head, out var inView) && inView
            ? head
            : new Vector2((min.X + max.X) * 0.5f, min.Y + (max.Y - min.Y) * 0.18f);

        var s1 = ImGui.CalcTextSize(line1);
        var s2 = ImGui.CalcTextSize(line2);
        var width = MathF.Max(s1.X, s2.X) + 34f;
        var height = s1.Y + s2.Y + 12f;
        var topLeft = new Vector2(at.X - width * 0.5f, at.Y - height);
        dl.AddRectFilled(topLeft, topLeft + new Vector2(width, height), Colors.Pack(Colors.Ink, 0.72f * appear), 8f);
        dl.AddRect(topLeft, topLeft + new Vector2(width, height), Colors.Pack(config.BeadColor, 0.6f * appear), 8f);
        DrawBead(dl, topLeft + new Vector2(14f, height * 0.5f), 4.5f * (0.92f + 0.16f * Pulse.Breath(t, 2.6f)), appear);
        dl.AddText(topLeft + new Vector2(26f, 5f), Colors.Pack(Colors.Ivory, appear), line1);
        dl.AddText(topLeft + new Vector2(26f, 7f + s1.Y), Colors.Pack(Colors.Ivory, 0.7f * appear), line2);
    }

    private static void Label(ImDrawListPtr dl, Vector2 centre, string text, float alpha)
    {
        var size = ImGui.CalcTextSize(text);
        var at = centre - new Vector2(size.X * 0.5f, 0f);
        dl.AddText(at + new Vector2(1f, 1f), Colors.Pack(Colors.Ink, 0.8f * alpha), text);
        dl.AddText(at, Colors.Pack(Colors.Ivory, alpha), text);
    }
}
