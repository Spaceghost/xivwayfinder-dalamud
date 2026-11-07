using System.Numerics;

namespace XivWayfinder.Core;

public enum CompanionTeleportMode { None, RequestStop, Corral, JoinCast, AwaitDeparture }

/// <summary>Observed game state only. This controller never starts a cast or controls the player.</summary>
public readonly record struct CompanionTeleportInput(uint Territory, uint Destination, Vector3 Player,
    float PlayerYaw, Vector3? GuidePosition, bool Visible, bool TeleportNeeded,
    bool TeleportCasting = false, float CastRemaining = 0);

/// <summary>
/// Rendezvous is a QUERY destination, never a rendered placement. A caller must
/// obtain a complete walkable route from the companion's accepted position and
/// walk it independently. LookAt/gestures apply only after validated arrival.
/// </summary>
public readonly record struct CompanionTeleportFrame(CompanionTeleportMode Mode, Vector3? Rendezvous,
    Vector3 LookAt, long Revision, bool RequestRest, bool RequestBeckon, bool ReadyToJoin)
{
    public bool Active => Mode != CompanionTeleportMode.None && Rendezvous is not null;
}

/// <summary>
/// A companion asks the player to stop for a teleport. Continued actual motion
/// produces occasional alternating ahead/side rendezvous requests (sheepdog
/// behavior); it does not orbit on a timer or attach a model to player offsets.
/// While a real Teleport cast is observed, it requests a nearby boarding spot.
/// Authored gestures and boarding animation remain the renderer's responsibility.
/// </summary>
public sealed class CompanionTeleportIntent
{
    public const double RetargetInterval = 1.1;
    public const double CorralRetargetInterval = CompanionApproachCoordinator.CorralRequestInterval;
    public const float CorralAnticipation = 1f, MaximumCorralLead = 8f;
    public const float RetargetDistance = .85f;
    private uint territory, destination;
    private Vector3 previousPlayer, requestedAt;
    private Vector2 velocity, heading = Vector2.UnitY;
    private double previousTime = double.NaN, lastRequest = double.NegativeInfinity, lastSideChange;
    private float movingSeconds, stillSeconds;
    private bool moving, wasCasting;
    private int side = 1;
    private long revision;
    private Vector3? rendezvous;
    public CompanionTeleportFrame Current { get; private set; }
    public float ObservedSpeed => velocity.Length();
    public float ApproachCruiseSpeed => float.IsFinite(ObservedSpeed)
        ? Math.Clamp(ObservedSpeed + 2.5f, 3.5f, CompanionRouteMotion.MaximumSpeed) : float.NaN;

    public void Reset()
    {
        territory = destination = 0;
        previousPlayer = requestedAt = default;
        velocity = default; heading = Vector2.UnitY;
        previousTime = double.NaN; lastRequest = double.NegativeInfinity; lastSideChange = 0;
        movingSeconds = stillSeconds = 0; moving = wasCasting = false; side = 1; rendezvous = null;
        // Monotonic request identity prevents a reset/new destination from
        // accidentally accepting an asynchronous response from the prior cue.
        Current = new(CompanionTeleportMode.None, null, default, ++revision, false, false, false);
    }

    public CompanionTeleportFrame Update(CompanionTeleportInput input, double now)
    {
        if (!input.Visible || input.Territory == 0 || !Valid(input.Player) || !float.IsFinite(input.PlayerYaw)
            || (!input.TeleportNeeded && !input.TeleportCasting))
        { if (Current.Active || !double.IsNaN(previousTime)) Reset(); return Current; }
        if (!double.IsFinite(now)) return Current;
        if (territory != input.Territory || destination != input.Destination || double.IsNaN(previousTime))
        {
            Reset(); territory = input.Territory; destination = input.Destination;
            previousPlayer = input.Player; previousTime = now;
            heading = new(MathF.Sin(input.PlayerYaw), MathF.Cos(input.PlayerYaw));
            lastSideChange = now;
        }

        var elapsed = now - previousTime;
        var delta = Flat(input.Player - previousPlayer);
        if (elapsed < 0 || elapsed > .5 || delta.Length() > 8 || Math.Abs(input.Player.Y - previousPlayer.Y) > 8)
        {
            // A clock pause, warp or layer change is not running away. Consume
            // it without banking a burst of velocity or repeatedly orbiting.
            velocity = default; moving = false; movingSeconds = stillSeconds = 0;
            previousPlayer = input.Player; previousTime = now;
            lastRequest = lastSideChange = now;
        }
        else if (elapsed > 0)
        {
            var dt = (float)Math.Min(elapsed, .1);
            var observed = delta / (float)elapsed;
            velocity += (observed - velocity) * (1 - MathF.Exp(-dt / .18f));
            moving = velocity.Length() > (moving ? .25f : .55f);
            if (moving && delta.LengthSquared() > .000001f)
            {
                var nextHeading = Vector2.Normalize(velocity);
                // Normalized vector lerp cannot turn an exactly opposite unit
                // vector with a small frame weight; it remains stuck forever.
                var angle = Heading.RotationOf(heading);
                var turn = Heading.Wrap(Heading.RotationOf(nextHeading) - angle);
                heading = Heading.Forward(angle + turn * (1 - MathF.Exp(-dt / .22f)));
            }
            movingSeconds = moving ? movingSeconds + dt : 0;
            stillSeconds = moving ? 0 : stillSeconds + dt;
            previousPlayer = input.Player; previousTime = now;
        }

        var casting = input.TeleportCasting && float.IsFinite(input.CastRemaining) && input.CastRemaining >= 0;
        var mode = casting
            ? input.CastRemaining <= .2f ? CompanionTeleportMode.AwaitDeparture : CompanionTeleportMode.JoinCast
            : movingSeconds >= .3f ? CompanionTeleportMode.Corral : CompanionTeleportMode.RequestStop;
        var castChanged = casting != wasCasting;
        var modeChanged = mode != Current.Mode;
        var movedSinceRequest = Flat(input.Player - requestedAt).Length();
        // Compare with the player at request time, not the anticipated point:
        // a legitimate eight-yalm lookahead is not eight yalms of player drift.
        var tooFar = rendezvous is { } old && (movedSinceRequest > 5 || Math.Abs(input.Player.Y - old.Y) > 1);
        var interval = mode == CompanionTeleportMode.Corral ? CorralRetargetInterval : RetargetInterval;
        var request = rendezvous is null || castChanged || modeChanged || tooFar
            || now - lastRequest >= interval && movedSinceRequest >= RetargetDistance;
        if (request)
        {
            // Alternate only after the player has actually kept moving. A
            // stationary player or a turn in place does not make the guide pace.
            if (mode == CompanionTeleportMode.Corral && now - lastSideChange >= 3)
            { side = -side; lastSideChange = now; }
            var right = new Vector2(heading.Y, -heading.X);
            var lead = Math.Min(MaximumCorralLead, 2.1f + ObservedSpeed * CorralAnticipation);
            var offset = casting ? right * .45f + heading * .1f
                : mode == CompanionTeleportMode.Corral ? heading * lead + right * (side * .7f)
                : heading * 1.65f + right * (side * .35f);
            var next = input.Player + new Vector3(offset.X, 0, offset.Y);
            if (rendezvous is null || Vector3.Distance(rendezvous.Value, next) > .05f || castChanged)
            { rendezvous = next; revision++; }
            requestedAt = input.Player; lastRequest = now;
        }
        wasCasting = casting;
        var at = input.GuidePosition is { } guide && Valid(guide) && rendezvous is { } goal
            && Flat(guide - goal).Length() <= .8f && Math.Abs(guide.Y - goal.Y) <= .65f;
        var closeToPlayer = input.GuidePosition is { } companion && Valid(companion)
            && Flat(companion - input.Player).Length() <= .9f && Math.Abs(companion.Y - input.Player.Y) <= .65f;
        Current = new(mode, rendezvous, input.Player, revision,
            at && !casting && !moving && stillSeconds >= 1.2f,
            at && !casting && (moving || stillSeconds < 1.2f),
            casting && closeToPlayer && !moving);
        return Current;
    }

    private static Vector2 Flat(Vector3 value) => new(value.X, value.Z);
    private static bool Valid(Vector3 value) => TrailPlacement.IsFinite(value)
        && Math.Abs(value.X) <= 1_000_000 && Math.Abs(value.Y) <= 1_000_000 && Math.Abs(value.Z) <= 1_000_000;
}
