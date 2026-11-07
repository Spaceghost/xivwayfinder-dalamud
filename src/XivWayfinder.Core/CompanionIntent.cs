using System.Numerics;

namespace XivWayfinder.Core;

public enum CompanionIntentMode { Lead, Returning, Waiting }

/// <summary>
/// Desired route offset/attention, not a world position or player movement command.
/// CruiseSpeed is zero for the mode default, or a finite 1..9 yalm/s override.
/// </summary>
public readonly record struct CompanionIntentFrame(CompanionIntentMode Mode, float EffectiveLead,
    float GoalGlanceWeight, bool FacingTravel, float CruiseSpeed = 0);

/// <summary>
/// A loyal companion's intent, inferred only from actual displacement and validated
/// route progress. Facing changes alone never select a quest or start leading.
/// The separate route-motion controller walks toward this desired offset at its
/// own bounded speed. It must not attach the guide directly to player+offset.
/// </summary>
public sealed class CompanionIntent
{
    private const float NearLead = 1f;
    private const float RetreatDelay = .35f, ResumeDelay = .25f, PauseDelay = .9f;
    private Vector3 previousPlayer;
    private float previousArc, speed, lead = NearLead, glance;
    private double previousTime = double.NaN, attentionTime;
    private float retreatTime, advanceTime, pauseTime;
    private bool moving;
    public CompanionIntentFrame Current { get; private set; } = new(CompanionIntentMode.Waiting, NearLead, 0, false);

    public void Reset()
    {
        previousTime = double.NaN; previousPlayer = default; previousArc = speed = glance = 0;
        lead = NearLead; moving = false; retreatTime = advanceTime = pauseTime = 0; attentionTime = 0;
        Current = new(CompanionIntentMode.Waiting, NearLead, 0, false);
    }

    public CompanionIntentFrame Update(Vector3 player, float routeArc, Vector2 tangent,
        double timeSeconds, float normalLead, bool hasRoute)
    {
        if (!hasRoute || !Finite(player) || !float.IsFinite(routeArc) || routeArc < 0
            || !float.IsFinite(tangent.X) || !float.IsFinite(tangent.Y) || tangent.LengthSquared() < 1e-8f)
        { Reset(); return Current; }
        if (!double.IsFinite(timeSeconds)) return Current;
        normalLead = float.IsFinite(normalLead) ? Math.Clamp(normalLead, NearLead, 16f) : 4f;
        if (double.IsNaN(previousTime))
        {
            Remember();
            return Current;
        }
        var elapsed = timeSeconds - previousTime;
        if (elapsed == 0) return Current;
        var delta = new Vector2(player.X - previousPlayer.X, player.Z - previousPlayer.Z);
        var distance = delta.Length(); var progress = routeArc - previousArc;
        var discontinuity = elapsed < 0 || elapsed > .5 || distance > 8
            || Math.Abs(player.Y - previousPlayer.Y) > 8 || Math.Abs(progress) > distance * 2 + .5f;
        var dt = (float)Math.Clamp(elapsed, 0, .1);
        Remember();
        if (dt <= 0 && !discontinuity) return Current;
        var mode = Current.Mode;
        if (discontinuity)
        {
            // Loading/repath/time reversal is not evidence of running toward or
            // away from a goal. Keep the current offset and forget old intent.
            speed = 0; moving = false; retreatTime = advanceTime = pauseTime = 0;
            mode = lead > NearLead + .07f ? CompanionIntentMode.Returning : CompanionIntentMode.Waiting;
        }
        else
        {
            var actualSpeed = distance / dt;
            speed += (actualSpeed - speed) * (1 - MathF.Exp(-dt / .15f));
            moving = speed > (moving ? .12f : .3f);
            var goalDistance = Math.Max(Vector2.Dot(delta, Vector2.Normalize(tangent)), progress);
            // Route progress handles a turn whose new tangent is perpendicular
            // to this frame's movement. Displacement is always required too.
            var advancing = moving && distance > .001f && goalDistance > distance * .2f;
            advanceTime = advancing ? advanceTime + dt : 0;
            retreatTime = moving && !advancing && distance > .001f ? retreatTime + dt : 0;
            pauseTime = moving ? 0 : pauseTime + dt;
            if (mode != CompanionIntentMode.Lead && advanceTime >= ResumeDelay)
                mode = CompanionIntentMode.Lead;
            else if (mode == CompanionIntentMode.Lead && (retreatTime >= RetreatDelay || pauseTime >= PauseDelay))
                mode = CompanionIntentMode.Returning;
            else if (mode == CompanionIntentMode.Waiting && moving && !advancing)
                mode = CompanionIntentMode.Returning;
        }

        var desiredLead = mode == CompanionIntentMode.Lead ? normalLead : NearLead;
        var change = (desiredLead - lead) * (1 - MathF.Exp(-dt / .4f));
        lead += Math.Clamp(change, -4f * dt, 4f * dt);
        lead = Math.Clamp(lead, NearLead, 16f);
        if (mode == CompanionIntentMode.Returning && !moving && Math.Abs(lead - NearLead) < .07f)
            mode = CompanionIntentMode.Waiting;
        attentionTime += dt;
        var phase = (float)(attentionTime % 4.5);
        var pulse = phase is > 3 and < 4.4f ? MathF.Pow(MathF.Sin((phase - 3) / 1.4f * MathF.PI), 2) : 0;
        var wantedGlance = mode == CompanionIntentMode.Lead ? 1
            : pulse * (mode == CompanionIntentMode.Waiting ? .65f : .3f);
        glance += (wantedGlance - glance) * (1 - MathF.Exp(-dt / .14f));
        Current = new(mode, lead, Math.Clamp(glance, 0, 1), moving || mode == CompanionIntentMode.Returning);
        return Current;

        void Remember() { previousPlayer = player; previousArc = routeArc; previousTime = timeSeconds; }
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
