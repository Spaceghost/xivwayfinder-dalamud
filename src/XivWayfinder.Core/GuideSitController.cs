using System.Numerics;

namespace XivWayfinder.Core;

public enum GuideSitPhase { Standing, Enter, Seated, Exit }

/// <summary>Three authored clips belonging to one rig, not a generic chair animation.</summary>
public readonly record struct GuideSitClips(int Enter, int Loop, int Exit,
    float EnterSeconds, float LoopSeconds, float ExitSeconds)
{
    public bool Valid => Enter >= 0 && Loop >= 0 && Exit >= 0 && Enter != Loop && Loop != Exit && Enter != Exit
        && Duration(EnterSeconds) && Duration(LoopSeconds) && Duration(ExitSeconds);
    private static bool Duration(float value) => float.IsFinite(value) && value is >= .1f and <= 30;
}

public readonly record struct GuideSitSample(GuideSitPhase Phase, int Clip, float Time, float Rate, bool Restart)
{
    public bool Active => Phase != GuideSitPhase.Standing;
}

/// <summary>
/// A reversible request, not a reversible animation: withdrawing a sit finishes
/// its authored enter before playing its authored exit. Never releases movement
/// from the middle of a pose or on a clock gap. Reset is for hidden/destroyed actors.
/// </summary>
public sealed class GuideSitController
{
    private GuideSitClips clips;
    private GuideSitPhase phase;
    private float time, previousRate;
    private long sequence;
    private bool leave, exitComplete;
    public bool CanMove => phase == GuideSitPhase.Standing;
    public void Reset() { clips = default; phase = default; time = previousRate = 0; sequence = 0; leave = exitComplete = false; }
    public void Hold() => previousRate = 0;

    public GuideSitSample Update(GuideSitClips available, GuideGestureFrame? request, float dt, bool canStart)
    {
        var wantsSit = request is { Valid: true, Kind: GuideGestureKind.SitGround };
        var paused = request is { Paused: true };
        var validTick = float.IsFinite(dt) && dt > 0 && dt <= .25f;
        var restart = false;
        if (CanMove)
        {
            if (!wantsSit || !canStart || !available.Valid || paused || !validTick) return default;
            clips = available; sequence = request!.Value.SequenceId; phase = GuideSitPhase.Enter;
            time = 0; leave = false; restart = true;
        }
        else
        {
            // An asset/rig change requires a renderer lifecycle reset, never a
            // silent stand-up using different or missing animation metadata.
            if (clips != available) { previousRate = 0; return Sample(false, 0); }
            if (exitComplete)
            {
                // The complete authored standing endpoint has been submitted
                // for one update at the held root before movement is released.
                Reset(); return default;
            }
            leave |= !wantsSit || request!.Value.SequenceId != sequence;
            // Accumulate the rate used during the preceding render interval.
            // Native playback and exact imported sampling share this clock.
            if (validTick) time += dt * previousRate;
            if (phase == GuideSitPhase.Enter && time >= clips.EnterSeconds)
            { phase = leave ? GuideSitPhase.Exit : GuideSitPhase.Seated; time = 0; restart = true; }
            else if (phase == GuideSitPhase.Seated && leave)
            { phase = GuideSitPhase.Exit; time = 0; restart = true; }
            else if (phase == GuideSitPhase.Exit && time >= clips.ExitSeconds)
            { time = clips.ExitSeconds; previousRate = 0; exitComplete = true; return Sample(false, 0); }
            if (phase == GuideSitPhase.Seated) time %= clips.LoopSeconds;
        }
        previousRate = !paused && validTick ? 1 : 0;
        return Sample(restart, previousRate);
    }

    private GuideSitSample Sample(bool restart, float rate) => new(phase,
        phase == GuideSitPhase.Enter ? clips.Enter : phase == GuideSitPhase.Seated ? clips.Loop : clips.Exit,
        time, rate, restart);
}

public static class GuideSitAnimation
{
    /// <summary>Names alone discover a candidate, but do not advertise a supported imported sit.</summary>
    public static GuideSitClips Find(IReadOnlyList<GuideAnimationClip> clips)
    {
        var enter = -1; var loop = -1; var exit = -1;
        for (var i = 0; i < clips.Count; i++)
        {
            var name = new string(clips[i].Name.ToLowerInvariant().Where(char.IsLetter).ToArray());
            if (enter < 0 && name is "groundsitstart" or "sitgroundstart" or "sitongroundstart" or "groundsitenter" or "groundsittingdown") enter = i;
            if (loop < 0 && name is "groundsitidle" or "sitgroundidle" or "sitongroundidle" or "groundsittingidle" or "groundsitloop") loop = i;
            if (exit < 0 && name is "groundsitend" or "sitgroundend" or "sitongroundend" or "groundsitexit" or "groundstandup" or "standupfromground") exit = i;
        }
        return enter >= 0 && loop >= 0 && exit >= 0
            ? new(enter, loop, exit, clips[enter].DurationSeconds, clips[loop].DurationSeconds, clips[exit].DurationSeconds) : default;
    }

    /// <summary>
    /// Verify explicitly named imported phases join at their authored endpoints
    /// and return to the model's standing idle. Unknown or incompatible triples
    /// remain unsupported; no synthetic stand-up blend replaces a missing exit.
    /// </summary>
    public static GuideSitClips MeasureImported(GuideMesh mesh, CancellationToken cancel = default)
    {
        var clips = Find(mesh.Animations);
        var idle = GuideGestureAnimation.Imported(mesh.Animations).Wait;
        if (!clips.Valid || idle < 0 || mesh.animation is null || mesh.Vertices.Length is < 1 or > 200000) return default;
        var a = new Vector3[mesh.Vertices.Length]; var b = new Vector3[a.Length];
        bool Match(int first, float firstTime, int second, float secondTime)
        {
            cancel.ThrowIfCancellationRequested();
            mesh.SamplePose(first, firstTime, a, loop: false, inPlace: true);
            mesh.SamplePose(second, secondTime, b, loop: false, inPlace: true);
            return EndpointsMatch(a, b);
        }
        return !Match(idle, 0, clips.Loop, 0) // a renamed idle is not a seated pose
            && Match(idle, 0, clips.Enter, 0)
            && Match(clips.Enter, clips.EnterSeconds, clips.Loop, 0)
            && Match(clips.Loop, 0, clips.Loop, clips.LoopSeconds)
            && Match(clips.Loop, 0, clips.Exit, 0)
            && Match(clips.Exit, clips.ExitSeconds, idle, 0) ? clips : default;
    }

    public static bool EndpointsMatch(ReadOnlySpan<Vector3> a, ReadOnlySpan<Vector3> b)
    {
        if (a.IsEmpty || a.Length != b.Length) return false;
        var low = float.PositiveInfinity; var high = float.NegativeInfinity;
        foreach (var p in a)
        { if (!TrailPlacement.IsFinite(p)) return false; low = Math.Min(low, p.Y); high = Math.Max(high, p.Y); }
        var tolerance = Math.Max(.005f, (high - low) * .04f);
        for (var i = 0; i < a.Length; i++)
            if (!TrailPlacement.IsFinite(b[i]) || Vector3.Distance(a[i], b[i]) > tolerance) return false;
        return true;
    }
}
