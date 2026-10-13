using System.Numerics;

namespace XivWayfinder.Core;

public delegate CompanionApproachSelection CompanionApproachSelector(uint zone, Vector3 rendezvous,
    Vector3 player, CompanionRouteMotionFrame accepted, double now, bool enabled);

/// <summary>Placement feedback is still required whenever MotionSubmitted is true.</summary>
public readonly record struct CompanionTeleportMotionFrame(CompanionRouteMotionFrame Motion,
    bool MotionSubmitted, bool AtDestination);

/// <summary>
/// Connects a teleport rendezvous request to owned route motion. It cannot cast,
/// control the player, fabricate a connector, or accept renderer placement.
/// An airborne companion finishes its owned jump before querying or rebasing.
/// </summary>
public sealed class CompanionTeleportMotion(CompanionApproachSelector select)
{
    public CompanionTeleportMotionFrame Update(CompanionRouteMotion motion, CompanionJumpTraversal jump,
        uint zone, Vector3 player, CompanionTeleportFrame cue, CompanionIntentFrame intent,
        GuideJumpCapability capability, double now, bool canMove)
    {
        // Even an interrupted cast or a newly requested rendezvous cannot
        // redirect an airborne guide to an unrelated route occurrence.
        if (jump.Active)
        {
            var owned = motion.Current;
            return new(jump.Update(motion, zone, owned.Route, owned.Arc, intent, capability, now,
                owned.HasPosition), true, false);
        }

        // Queries must originate at the last *accepted* placement. Forgetting
        // feedback is not permission to adopt a proposed visual position.
        jump.ResolvePlacement(motion, false);
        motion.ResolvePlacement(false);
        if (!cue.Active || zone == 0 || !double.IsFinite(now)) return Hold(false);

        CompanionApproachSelection selected;
        try { selected = select(zone, cue.Rendezvous!.Value, player, motion.Current, now, true); }
        catch (Exception) { return Hold(false); }
        if (cue.Mode != CompanionTeleportMode.Corral && selected.Ready && selected.Route is { Length: >= 2 }
            && (Heading.FlatDistance(selected.Route[^1], cue.Rendezvous.Value) > CompanionApproachCoordinator.DestinationHorizontalTolerance
                || Math.Abs(selected.Route[^1].Y - cue.Rendezvous.Value.Y) > CompanionApproachCoordinator.DestinationVerticalTolerance))
            return Hold(false); // Stop/join requests never keep pacing toward a superseded corral endpoint.
        if (!canMove || selected.AtDestination) return Hold(selected.AtDestination);
        if (!selected.Ready)
            return selected.QueryPending ? new(motion.HoldQueryClock(now), false, false) : Hold(false);
        if (selected.HoldPosition) return Hold(false);
        if (!GuideConnectorPlan.Valid(selected.Route) || !float.IsFinite(selected.ProgressArc)
            || selected.ProgressArc < 0 || selected.ProgressArc > PathTrail.Length(selected.Route))
            return Hold(false);

        if (!motion.Current.HasPosition)
        {
            // The endpoint is a target, never a spawn location. Initial
            // placement must be rendered/accepted before the first step.
            if (selected.RebaseArc != 0) return Hold(false);
            return new(jump.Update(motion, zone, selected.Route, 0, intent, capability, now, true), true, false);
        }
        if (selected.RebaseArc is { } at)
        {
            if (at != 0 || !motion.TryRebase(selected.Route, 0)) return Hold(false);
        }
        else if (!ReferenceEquals(motion.Current.Route, selected.Route))
            return Hold(false); // Never fall through to a global nearest-point rebase.
        return new(jump.Update(motion, zone, selected.Route, selected.ProgressArc, intent, capability, now, true),
            true, false);

        CompanionTeleportMotionFrame Hold(bool arrived) => new(motion.HoldJumpClock(now), false, arrived);
    }
}
