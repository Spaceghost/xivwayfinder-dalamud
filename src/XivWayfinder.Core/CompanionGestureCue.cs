namespace XivWayfinder.Core;

/// <summary>
/// Framework-owned gesture request clock. A refused visual proposal restores
/// the accepted gesture phase, and consumed time can never become catch-up.
/// Renderers still verify their own authored clips and safely exit seated poses.
/// </summary>
public sealed class CompanionGestureCue
{
    private GuideGestureFrame? accepted;
    private double previousTime = double.NaN;
    private long sequence;
    private bool pending;
    public GuideGestureFrame? Current { get; private set; }

    public void Reset()
    {
        accepted = Current = null; previousTime = double.NaN; pending = false;
        sequence++; // Never reuse a renderer's prior lifecycle sequence.
    }

    public GuideGestureFrame? Propose(GuideGestureKind? kind, double now, bool paused = false)
    {
        if (pending) Resolve(false);
        var elapsed = double.IsFinite(now) && double.IsFinite(previousTime) ? now - previousTime : 0;
        if (double.IsFinite(now)) previousTime = now;
        if (kind is null || kind is not (GuideGestureKind.Wait or GuideGestureKind.Beckon or GuideGestureKind.SitGround))
        { accepted = Current = null; return null; }
        paused |= !double.IsFinite(now) || elapsed < 0 || elapsed > .5;
        Current = accepted is { } prior && prior.Kind == kind
            ? prior with { ElapsedSeconds = prior.ElapsedSeconds + (paused ? 0 : (float)Math.Min(elapsed, .1)), Paused = paused }
            : new(kind.Value, ++sequence, 0, paused);
        pending = true;
        return Current;
    }

    public GuideGestureFrame? Resolve(bool placementAccepted)
    {
        if (!pending) return Current;
        pending = false;
        if (placementAccepted) accepted = Current;
        else Current = accepted is { } prior ? prior with { Paused = true } : null;
        return Current;
    }
}
