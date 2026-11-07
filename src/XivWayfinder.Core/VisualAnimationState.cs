namespace XivVisualAssets.Animation;

/// <summary>Authored clip indices supplied by the consuming client visual. -1 means its bind pose.</summary>
public readonly record struct LocomotionClips(int Idle, int Walk, int Run);

/// <summary>Two authored samples and their pose blend, independent of any game, skeleton or renderer.</summary>
public readonly record struct VisualAnimationFrame(int FromClip, float FromTime, int ToClip, float ToTime, float Blend)
{
    public bool Blending => FromClip != ToClip && Blend < 1;
}

/// <summary>Movement units are those supplied by the client. Speeds are configurable for other visual consumers.</summary>
public sealed record LocomotionPolicy(
    float StartSpeed = .2f, float StopSpeed = .08f,
    float RunStartSpeed = 3.2f, float RunStopSpeed = 2.6f,
    float WalkReferenceSpeed = 1.8f, float RunReferenceSpeed = 4.5f,
    float FadeSeconds = .18f, float MinPlaybackRate = .35f, float MaxPlaybackRate = 1.8f);

/// <summary>
/// Client-only locomotion playback: hysteresis avoids idle/walk/run chatter;
/// clip clocks advance continuously at movement-scaled rates. Reversing a
/// transition preserves its current mixture. A third clip waits for the short
/// current fade to finish rather than snapping to an unblended pose.
/// </summary>
public sealed class VisualAnimationState
{
    private readonly float[] durations;
    private readonly LocomotionPolicy policy;
    private bool initialized, moving, running;
    private int from = -1, to = -1;
    private double fromTime, toTime;
    private float progress = 1;

    public VisualAnimationState(IEnumerable<float> durations, LocomotionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(durations);
        this.durations = durations.ToArray();
        if (this.durations.Any(d => !float.IsFinite(d) || d < 0))
            throw new ArgumentException("Clip durations must be finite and nonnegative.", nameof(durations));
        this.policy = policy ?? new();
        var p = this.policy;
        if (!float.IsFinite(p.StartSpeed) || !float.IsFinite(p.StopSpeed) || p.StopSpeed < 0 || p.StartSpeed <= p.StopSpeed
            || !float.IsFinite(p.RunStartSpeed) || !float.IsFinite(p.RunStopSpeed) || p.RunStopSpeed <= p.StartSpeed || p.RunStartSpeed <= p.RunStopSpeed
            || !float.IsFinite(p.WalkReferenceSpeed) || p.WalkReferenceSpeed <= 0 || !float.IsFinite(p.RunReferenceSpeed) || p.RunReferenceSpeed <= 0
            || !float.IsFinite(p.FadeSeconds) || p.FadeSeconds <= 0 || !float.IsFinite(p.MinPlaybackRate) || p.MinPlaybackRate <= 0
            || !float.IsFinite(p.MaxPlaybackRate) || p.MaxPlaybackRate < p.MinPlaybackRate)
            throw new ArgumentException("Invalid locomotion thresholds, playback rates or fade duration.", nameof(policy));
    }

    public void Reset()
    {
        initialized = moving = running = false;
        from = to = -1; fromTime = toTime = 0; progress = 1;
    }

    public VisualAnimationFrame Update(LocomotionClips clips, float speed, double deltaSeconds, bool animate = true, int? pinnedClip = null)
    {
        Validate(clips.Idle); Validate(clips.Walk); Validate(clips.Run);
        if (pinnedClip is { } pin) Validate(pin);
        if (!animate) { Reset(); return new(-1, 0, -1, 0, 1); }
        var dt = double.IsFinite(deltaSeconds) ? (float)Math.Clamp(deltaSeconds, 0, .1) : 0;
        speed = float.IsFinite(speed) ? Math.Max(speed, 0) : 0;
        moving = speed > (moving ? policy.StopSpeed : policy.StartSpeed);
        running = moving && speed > (running ? policy.RunStopSpeed : policy.RunStartSpeed);
        var desired = pinnedClip ?? (moving ? running ? clips.Run : clips.Walk : clips.Idle);
        if (desired < 0 && moving && pinnedClip is null)
            desired = (running ? clips.Walk : clips.Run) is >= 0 and var alternative ? alternative : clips.Idle;

        if (!initialized)
        {
            initialized = true;
            from = to = desired; fromTime = toTime = 0; progress = 1;
            return Frame();
        }

        fromTime = Advance(from, fromTime, dt * Rate(from));
        toTime = Advance(to, toTime, dt * Rate(to));
        progress = Math.Min(1, progress + dt / policy.FadeSeconds);
        if (progress >= 1) { from = to; fromTime = toTime; }

        if (desired != to)
        {
            if (progress >= 1)
            {
                from = to; fromTime = toTime; to = desired;
                // Walk/run changes preserve normalized gait phase. Idle or
                // explicit pinned clips start at their authored first frame.
                toTime = moving && pinnedClip is null && from != clips.Idle && from >= 0 && to >= 0
                    && durations[from] > 0 ? fromTime / durations[from] * durations[to] : 0;
                progress = 0;
            }
            else if (desired == from)
            {
                (from, to) = (to, from); (fromTime, toTime) = (toTime, fromTime);
                progress = 1 - progress;
            }
            // A different third clip remains the desired target next frame;
            // let this bounded fade finish before starting that transition.
        }
        return Frame();

        float Rate(int clip)
        {
            if (pinnedClip is not null || clip < 0 || clip == clips.Idle) return 1;
            var reference = clip == clips.Run && clips.Run != clips.Walk ? policy.RunReferenceSpeed : policy.WalkReferenceSpeed;
            return Math.Clamp(speed / reference, policy.MinPlaybackRate, policy.MaxPlaybackRate);
        }
    }

    private VisualAnimationFrame Frame()
    {
        var blend = progress * progress * (3 - 2 * progress);
        return new(from, (float)fromTime, to, (float)toTime, blend);
    }

    private double Advance(int clip, double time, float seconds) => clip >= 0 && durations[clip] > 0
        ? (time + seconds) % durations[clip] : 0;

    private void Validate(int clip)
    {
        if (clip < -1 || clip >= durations.Length) throw new ArgumentOutOfRangeException(nameof(clip));
    }
}
