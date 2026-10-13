using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>
/// Owns one evidenced, authored companion jump. The normal arc integrator is
/// never run while airborne. Every proposal must be acknowledged after native
/// placement; rejecting it restores both the owned arc and displayed clip phase.
/// No player state, collision service or renderer is accessed by this controller.
/// </summary>
public sealed class CompanionJumpTraversal(
    Func<uint, Vector3[], float, int, GuideJumpCapability, double, GuideJumpProbeResult> probe,
    Func<GuideJumpFrame, GuideJumpFrame, GuideJumpEvidence> validate)
{
    private GuideJumpPlan? plan;
    private GuideJumpFrame? acceptedFrame;
    private uint territory;
    private double previousTime = double.NaN;
    private bool placementPending, jumpProposal;

    public GuideJumpFrame? Frame { get; private set; }
    public bool Active => plan is not null;
    public GuideJumpEvidence Evidence { get; private set; } = GuideJumpEvidence.Unknown;
    public string Status { get; private set; } = "No active companion jump.";

    public void Reset()
    {
        plan = null; acceptedFrame = Frame = null; territory = 0;
        previousTime = double.NaN; placementPending = jumpProposal = false;
        Evidence = GuideJumpEvidence.Unknown; Status = "No active companion jump.";
    }

    public CompanionRouteMotionFrame Update(CompanionRouteMotion motion, uint zone, Vector3[] selectedRoute,
        float progressArc, CompanionIntentFrame intent, GuideJumpCapability capability, double now, bool hasRoute)
    {
        // Forgetting the renderer acknowledgement is not permission to advance
        // an invisible companion. Roll back that proposal before doing new work.
        if (placementPending) ResolvePlacement(motion, false);
        if (!double.IsFinite(now)) { Status = "Waiting for a valid jump clock."; return motion.Current; }
        if (plan is not null)
            return Advance(motion, zone, capability, now, hasRoute);

        Frame = null; acceptedFrame = null;
        var before = motion.Current;
        var proposed = motion.Update(selectedRoute, progressArc, intent, now, hasRoute);
        placementPending = true; jumpProposal = false;
        Evidence = GuideJumpEvidence.Unknown;
        Status = "Following the walkable route.";
        if (!hasRoute || zone == 0 || !capability.Valid || !before.HasPosition || !proposed.HasPosition
            || before.NeedsConnection || proposed.NeedsConnection || !ReferenceEquals(before.Route, proposed.Route)
            || !ReferenceEquals(proposed.Route, selectedRoute) || Math.Abs(proposed.Arc - before.Arc) <= .000001f)
            return proposed;

        var direction = Math.Sign(proposed.Arc - before.Arc);
        GuideJumpProbeResult evidence;
        try { evidence = probe(zone, before.Route, before.Arc, direction, capability, now); }
        catch (Exception) { evidence = new(GuideJumpEvidence.Unknown, null, "Jump evidence is unavailable."); }
        Evidence = evidence.Evidence; Status = evidence.Reason;
        // Unknown/Blocked/Clear are not jump permission. Ordinary steps still
        // go through the renderer's usual native floor and wall checks.
        if (evidence.Evidence is not (GuideJumpEvidence.Pending or GuideJumpEvidence.Approved)) return proposed;

        motion.ResolvePlacement(false);
        placementPending = false;
        if (evidence.Evidence != GuideJumpEvidence.Approved || evidence.Plan is not { } approved) return motion.Current;
        if (!approved.Valid || !ReferenceEquals(approved.Route, before.Route) || !approved.Request.MatchesRoute()
            || approved.Request.Capability != capability || Math.Abs(approved.Request.StartArc - before.Arc) > .0001f
            || Math.Sign(approved.Request.EndArc - approved.Request.StartArc) != direction)
        { Evidence = GuideJumpEvidence.Unknown; Status = "Jump approval no longer matches the owned route."; return motion.Current; }

        var takeoff = approved.Sample(0);
        if (!motion.TryApplyJumpFrame(takeoff, now))
        { Evidence = GuideJumpEvidence.Unknown; Status = "Waiting for a matching takeoff position."; return motion.Current; }
        plan = approved; territory = zone; previousTime = now;
        Frame = takeoff; acceptedFrame = null;
        placementPending = jumpProposal = true;
        Status = "Companion takeoff proposed.";
        return motion.Current;
    }

    private CompanionRouteMotionFrame Advance(CompanionRouteMotion motion, uint zone,
        GuideJumpCapability capability, double now, bool hasRoute)
    {
        if (!hasRoute || zone == 0 || zone != territory || !plan!.Valid || plan.Request.Capability != capability
            || !capability.Valid || !ReferenceEquals(motion.Current.Route, plan.Route) || !plan.Request.MatchesRoute())
        {
            Reset(); Status = "Jump cancelled after route, model or zone availability changed.";
            return motion.HoldJumpClock(now); // lifecycle owner also resets/hides the renderer and motion
        }
        if (acceptedFrame is not { } previous)
        { Status = "Waiting for accepted takeoff placement."; return motion.HoldJumpClock(now); }
        var elapsed = now - previousTime;
        previousTime = now;
        Frame = previous;
        if (elapsed <= 0 || elapsed > .5)
        { Status = "Holding jump through a clock discontinuity."; return motion.HoldJumpClock(now); }

        // The collision validator accepts at most a quarter-clip step, including
        // short authored jumps after a slow frame. Never bank discarded time.
        var delta = (float)Math.Min(Math.Min(elapsed, .1) / plan.Duration, .24999);
        var next = plan.Sample(Math.Min(1, previous.Progress + delta));
        try { Evidence = validate(previous, next); }
        catch (Exception) { Evidence = GuideJumpEvidence.Unknown; }
        if (Evidence != GuideJumpEvidence.Clear)
        { Status = "Holding jump until the next body sweep is clear."; return motion.HoldJumpClock(now); }
        if (!motion.TryApplyJumpFrame(next, now))
        { Evidence = GuideJumpEvidence.Unknown; Status = "Holding an unaccepted jump step."; return motion.HoldJumpClock(now); }
        Frame = next; placementPending = jumpProposal = true;
        Status = next.Complete ? "Companion landing proposed." : "Companion jump step proposed.";
        return motion.Current;
    }

    public CompanionRouteMotionFrame ResolvePlacement(CompanionRouteMotion motion, bool accepted)
    {
        if (!placementPending) return motion.Current;
        placementPending = false;
        var result = motion.ResolvePlacement(accepted);
        if (!jumpProposal) return result;
        jumpProposal = false;
        if (!accepted)
        {
            Frame = acceptedFrame;
            if (acceptedFrame is null) { Reset(); Status = "Takeoff placement refused; staying on the accepted route."; }
            else Status = "Jump placement refused; holding the last accepted frame.";
            return result;
        }
        acceptedFrame = Frame;
        if (acceptedFrame is { Complete: true })
        { Reset(); Status = "Companion landed on the validated route."; }
        else Status = "Companion jump accepted.";
        return result;
    }
}
