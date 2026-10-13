using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>One route-constrained presentation shared by companion, lights and map.</summary>
public readonly record struct GuideChasePose(Vector3 Position, Vector3 LookAt, Vector2 Direction,
    Vector2 LightDirection, float GuideArc, float LookAheadArc);

/// <summary>
/// Active guidance follows a verified polyline, independently of legacy beside
/// preferences. Distances are measured from the player's tracked route arc.
/// Neither placement nor looking ahead invents a connector across a corner.
/// </summary>
public readonly record struct GuideChaseLayout(bool Chasing, float GuideLead, float LookAhead)
{
    public const float LightGap = .45f;
    public const float MaximumLead = 12;
    public const float MaximumAttentionDegrees = 12;
    private const int MaximumPathPoints = 4096;

    public static GuideChaseLayout Create(bool trailEnabled, float requestedLead, bool eager = false)
    {
        var lead = float.IsFinite(requestedLead) ? Math.Clamp(requestedLead, 2, MaximumLead) : 4;
        if (!trailEnabled) lead = Math.Max(6, lead);
        if (eager) lead = Math.Min(MaximumLead, lead + MinionGlove.EagerExtra);
        return new(trailEnabled, lead, lead + (trailEnabled ? 1.75f : 4));
    }

    /// <summary>Applies a loyal companion's dynamic lead without changing look distance.</summary>
    public GuideChaseLayout WithLead(float lead)
    {
        if (!float.IsFinite(lead)) return this;
        lead = Math.Clamp(lead, 0, MaximumLead);
        return this with { GuideLead = lead, LookAhead = lead + (Chasing ? 1.75f : 4) };
    }

    public GuideChasePose? Sample(ReadOnlySpan<Vector3> path, float progress)
    {
        if (!ValidPath(path, out var length) || !float.IsFinite(progress) || progress < 0
            || !float.IsFinite(GuideLead) || GuideLead is < 0 or > MaximumLead
            || !float.IsFinite(LookAhead) || LookAhead < GuideLead || LookAhead > MaximumLead + 4) return null;
        progress = Math.Min(progress, length);
        var arc = Math.Min(length, progress + GuideLead);
        return SampleAtArc(path, arc);
    }

    /// <summary>Samples the companion's independent route arc, including when returning behind the player.</summary>
    public GuideChasePose? SampleAtArc(ReadOnlySpan<Vector3> path, float arc)
    {
        if (!ValidPath(path, out var length) || !float.IsFinite(arc) || arc < 0
            || !float.IsFinite(LookAhead) || !float.IsFinite(GuideLead)
            || GuideLead is < 0 or > MaximumLead
            || LookAhead < GuideLead || LookAhead - GuideLead > 4) return null;
        arc = Math.Min(arc, length);
        var segment = Locate(path, arc, length);
        var at = Vector3.Lerp(path[segment.Index], path[segment.Index + 1],
            Math.Clamp((arc - segment.Start) / segment.Length, 0, 1));
        var lookArc = Math.Min(segment.End, arc + LookAhead - GuideLead);
        var look = Vector3.Lerp(path[segment.Index], path[segment.Index + 1],
            Math.Clamp((lookArc - segment.Start) / segment.Length, 0, 1));
        var direction = Heading.Flat(path[segment.Index], path[segment.Index + 1]) / segment.Length;
        var light = Heading.DirectionTo(at, look);
        if (light == Vector2.Zero) light = direction; // Endpoint: retain final route tangent, never jump beyond it.
        return new(at, look, direction, light, arc, lookArc);
    }

    /// <summary>Stops at the next corner; at the corner itself follows its outgoing segment.</summary>
    public static Vector3? CornerLimitedLookAhead(ReadOnlySpan<Vector3> path, float arc, float distance)
    {
        if (!ValidPath(path, out var length) || !float.IsFinite(arc) || arc < 0
            || !float.IsFinite(distance) || distance < 0) return null;
        arc = Math.Min(arc, length);
        var segment = Locate(path, arc, length);
        var target = Math.Min(segment.End, arc + distance);
        return Vector3.Lerp(path[segment.Index], path[segment.Index + 1],
            Math.Clamp((target - segment.Start) / segment.Length, 0, 1));
    }

    /// <summary>A smooth, occasional attention window; reduced motion suppresses it.</summary>
    public static float Attention(double timeSeconds, bool reducedMotion)
    {
        if (reducedMotion || !double.IsFinite(timeSeconds) || timeSeconds < 0) return 0;
        var cycle = timeSeconds % 8;
        if (cycle < 6) return 0;
        var wave = Math.Sin((cycle - 6) * Math.PI / 2);
        return (float)(wave * wave);
    }

    /// <summary>
    /// A small whole-model glance toward actual light flow, never a random
    /// head movement or an assertion of skeletal retargeting support.
    /// </summary>
    public static float AttentiveYaw(float travelYaw, float lightYaw, float attention)
    {
        if (!float.IsFinite(travelYaw)) return float.IsFinite(lightYaw) ? Heading.Wrap(lightYaw) : 0;
        if (!float.IsFinite(lightYaw) || !float.IsFinite(attention)) return Heading.Wrap(travelYaw);
        var limit = MaximumAttentionDegrees * MathF.PI / 180;
        var turn = Math.Clamp(Heading.Wrap(lightYaw - travelYaw), -limit, limit);
        return Heading.Wrap(travelYaw + turn * Math.Clamp(attention, 0, 1));
    }

    /// <summary>Turns the whole model gradually, at most 180 degrees per second.</summary>
    public static float EaseYaw(float current, float target, float dt)
    {
        if (!float.IsFinite(target)) return float.IsFinite(current) ? Heading.Wrap(current) : 0;
        if (!float.IsFinite(current)) return Heading.Wrap(target);
        if (!float.IsFinite(dt) || dt <= 0) return Heading.Wrap(current);
        dt = Math.Min(dt, .1f);
        var gap = Heading.Wrap(target - current);
        var amount = gap * (1 - MathF.Pow(.5f, dt / .18f));
        return Heading.Wrap(current + Math.Clamp(amount, -MathF.PI * dt, MathF.PI * dt));
    }

    private readonly record struct Segment(int Index, float Start, float Length)
    { public float End => Start + Length; }

    private static Segment Locate(ReadOnlySpan<Vector3> path, float arc, float total)
    {
        var start = 0f;
        var last = default(Segment);
        for (var i = 0; i + 1 < path.Length; ++i)
        {
            var length = Heading.FlatDistance(path[i], path[i + 1]);
            if (length <= 0) continue;
            last = new(i, start, length);
            if (arc < last.End || last.End >= total) return last;
            start += length;
        }
        return last;
    }

    private static bool ValidPath(ReadOnlySpan<Vector3> path, out float length)
    {
        length = 0;
        if (path.Length is < 2 or > MaximumPathPoints) return false;
        foreach (var point in path) if (!TrailPlacement.IsFinite(point)) return false;
        for (var i = 0; i + 1 < path.Length; ++i)
        {
            var segment = Heading.FlatDistance(path[i], path[i + 1]);
            if (!float.IsFinite(segment)) return false;
            length += segment;
        }
        return float.IsFinite(length) && length > .0001f;
    }
}
