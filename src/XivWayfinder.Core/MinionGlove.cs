using System.Numerics;

namespace XivWayfinder.Core;

/// <summary>Where the minion glove floats relative to the character, in yalms, and how it is turned.</summary>
/// <param name="Ahead">How far ahead of the character. Zero places a companion at the shoulder.</param>
/// <param name="Side">How far to the right (negative: to the left). Beside mode uses the character's heading.</param>
/// <param name="Height">Above the character's feet.</param>
/// <param name="TurnDegrees">Added to the yaw, for a model whose finger does not point along its own forward.</param>
/// <param name="MaxPitchDegrees">The steepest it tilts up or down a slope; 0 keeps it level.</param>
public readonly record struct MinionLayout(float Ahead, float Side, float Height, float TurnDegrees, float MaxPitchDegrees)
{
    public static MinionLayout Default => new(1.6f, 0.7f, 0.5f, 0f, 35f);
    public static MinionLayout BesideDefault => new(0f, 2f, 0.5f, 0f, 35f);

    public static float CompanionAhead(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 3f) : BesideDefault.Ahead;
    public static float CompanionSide(float value) => float.IsFinite(value) ? Math.Clamp(value, -2f, 2f) : BesideDefault.Side;
}

/// <summary>A pose for the minion glove: world position, yaw (character rotation convention) and pitch, radians.</summary>
public readonly record struct MinionPose(Vector3 Position, float Yaw, float Pitch);

/// <summary>
/// The minion glove: the game's own "Wind-up Cursor" minion model (the white pointing glove), shown as a purely
/// client-side object that floats beside the bead and points the way. Everything here is placement and timing,
/// testable without the game; the plugin's <c>GloveMinion</c> puts it in the world.
///
/// The minion is found at run time by its English name in the <c>Companion</c> sheet; its <c>Model</c> column is
/// a <c>ModelChara</c> row. Read from the game data of version 2026.09.15.0000.0000 (read-only): Companion row
/// <see cref="MeasuredCompanionRow"/> "wind-up cursor" (the sheet stores it lower case), Model
/// <see cref="MeasuredModelChara"/>, which is ModelChara Type 3 (monster), Model 8044, Base 1, Variant 1, i.e.
/// <c>chara/monster/m8044/obj/body/b0001/model/m8044b0001.mdl</c>. Those numbers are documentation and a test
/// fixture; the plugin matches by name and never ships or hardcodes the model.
/// </summary>
public static class MinionGlove
{
    /// <summary>The Companion sheet's singular name, compared ignoring case.</summary>
    public const string CompanionName = "Wind-up Cursor";

    public const uint MeasuredCompanionRow = 51;
    public const uint MeasuredModelChara = 469;

    /// <summary>Whether a Companion row's singular name is the Wind-up Cursor, whatever its case or padding.</summary>
    public static bool IsWindUpCursor(string? singular) =>
        singular is not null && string.Equals(singular.Trim(), CompanionName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where the glove floats this moment: <see cref="MinionLayout.Ahead"/> along <paramref name="dir"/> from the
    /// character, <see cref="MinionLayout.Side"/> to its right, bobbing gently, and pushed a little further along
    /// the way by the tap. Null when there is no direction.
    /// </summary>
    public static Vector3? Place(Vector3 player, Vector2 dir, MinionLayout layout, double t)
    {
        if (dir == Vector2.Zero || !float.IsFinite(dir.X) || !float.IsFinite(dir.Y))
            return null;
        var d = Vector2.Normalize(dir);
        var right = Right(d);
        var along = layout.Ahead + Pulse.Tap(t, 1.1f) * 0.18f;
        var height = layout.Height + Pulse.Bob(t, 2.2f, 0.08f);
        return new Vector3(
            player.X + d.X * along + right.X * layout.Side,
            player.Y + height,
            player.Z + d.Y * along + right.Y * layout.Side);
    }

    /// <summary>
    /// A companion beside the player's shoulder. Placement follows the player's heading, independently of
    /// where the route turns. There is no forward tap; floating companions may bob vertically. Grounded
    /// layouts (height zero) stay at feet level for the collision sampler to place on actual terrain.
    /// </summary>
    public static Vector3? PlaceBeside(Vector3 player, float playerRotation, MinionLayout layout, double t)
    {
        if (!float.IsFinite(playerRotation) || !float.IsFinite(player.X) || !float.IsFinite(player.Y) || !float.IsFinite(player.Z))
            return null;
        var forward = Heading.Forward(playerRotation);
        var side = Right(forward);
        var ahead = MinionLayout.CompanionAhead(layout.Ahead);
        var distance = MinionLayout.CompanionSide(layout.Side);
        var height = float.IsFinite(layout.Height) ? Math.Clamp(layout.Height, 0f, 12f) : MinionLayout.BesideDefault.Height;
        var bob = height > 0 ? Pulse.Bob(t, 2.2f, MathF.Min(.08f, height)) : 0;
        return new Vector3(player.X + forward.X * ahead + side.X * distance,
            player.Y + height + bob,
            player.Z + forward.Y * ahead + side.Y * distance);
    }

    /// <summary>
    /// Leading the way: the glove floats <paramref name="lead"/> yalms ahead of the character along the walkable
    /// route (<paramref name="path"/>, from vnavmesh), at the layout's height with a gentle bob, pointing on along the
    /// route from there, so following it is following the path. Near the end of the route it waits at the end. With
    /// no route it leads along <paramref name="dir"/> in a straight line. -> where it floats and the way it points,
    /// or null with no direction.
    /// </summary>
    /// <param name="eager">The Main Scenario: it leads a little further ahead and bounces, beckoning.</param>
    public static (Vector3 At, Vector2 Dir)? Lead(Vector3 player, ReadOnlySpan<Vector3> path, Vector2 dir, float lead, MinionLayout layout, double t, bool eager = false, float? progressArc = null)
    {
        lead = float.IsFinite(lead) ? Math.Clamp(lead, 1f, 12f) : 4f;
        if (eager)
            lead += EagerExtra;
        var bob = layout.Height + (eager ? Pulse.Bob(t, 1.1f, 0.16f) : Pulse.Bob(t, 2.2f, 0.08f));
        if (path.Length >= 2)
        {
            var arc = progressArc is { } tracked && float.IsFinite(tracked)
                ? Math.Clamp(tracked, 0, PathTrail.Length(path)) : PathTrail.Project(path, player, out _);
            var at = PathTrail.LookAhead(path, arc, lead);
            var on = PathTrail.LookAhead(path, arc, lead + 1.5f);
            var way = Heading.DirectionTo(at, on);
            if (way == Vector2.Zero)
                way = Heading.DirectionTo(player, at); // at the end: point on from where you are
            if (way != Vector2.Zero)
                return (new Vector3(at.X, at.Y + bob, at.Z), way);
        }

        if (dir == Vector2.Zero || !float.IsFinite(dir.X) || !float.IsFinite(dir.Y))
            return null;
        var d = Vector2.Normalize(dir);
        return (new Vector3(player.X + d.X * lead, player.Y + bob, player.Z + d.Y * lead), d);
    }

    /// <summary>How much further ahead the glove leads on the Main Scenario, yalms.</summary>
    public const float EagerExtra = 1.5f;

    /// <summary>
    /// The (X, Z) direction to the right of someone facing <paramref name="dir"/>. The world is X east, Z south, Y
    /// up: facing south (+Z) the right hand is west (−X).
    /// </summary>
    public static Vector2 Right(Vector2 dir) => new(-dir.Y, dir.X);

    /// <summary>The yaw that points the glove along <paramref name="dir"/>, plus the layout's turn, folded into (−π, π].</summary>
    public static float Yaw(Vector2 dir, float turnDegrees) =>
        Heading.Wrap(Heading.RotationOf(dir) + (float.IsFinite(turnDegrees) ? turnDegrees : 0f) * (MathF.PI / 180f));

    /// <summary>
    /// How steeply to tilt from <paramref name="from"/> towards <paramref name="to"/>: positive points up. Level
    /// when the target's height is unknown, when they are within half a yalm on the ground, and never beyond
    /// ±<paramref name="maxDegrees"/>.
    /// </summary>
    public static float Pitch(Vector3 from, Vector3 to, bool heightKnown, float maxDegrees)
    {
        if (!heightKnown || !(maxDegrees > 0f))
            return 0f;
        var flat = Heading.FlatDistance(from, to);
        var rise = to.Y - from.Y;
        if (!float.IsFinite(flat) || !float.IsFinite(rise) || flat < 0.5f)
            return 0f;
        var max = MathF.Min(maxDegrees, 89f) * (MathF.PI / 180f);
        return Math.Clamp(MathF.Atan2(rise, flat), -max, max);
    }

    /// <summary>
    /// The rotation for a model whose forward is +Z: turned by <paramref name="yaw"/> about the up axis (so +Z
    /// goes to (sin yaw, 0, cos yaw), the character convention), and tilted up by <paramref name="pitch"/>.
    /// </summary>
    public static Quaternion Orientation(float yaw, float pitch)
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.IsFinite(yaw) ? yaw : 0f);
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, float.IsFinite(pitch) ? -pitch : 0f);
        return Quaternion.Normalize(turn * tilt);
    }

    /// <summary>
    /// Eases an angle towards another the short way round, closing half the gap every <paramref name="halfLife"/>
    /// seconds, so the glove swings rather than snaps when the way turns.
    /// </summary>
    public static float EaseAngle(float current, float target, float dt, float halfLife)
    {
        if (!float.IsFinite(current))
            return Heading.Wrap(target);
        var gap = Heading.Wrap(target - current);
        var closed = Pulse.Approach(0f, gap, dt, halfLife);
        return Heading.Wrap(current + closed);
    }

    /// <summary>Eases a position; a jump of more than <paramref name="snap"/> yalms (a teleport, a zone) is taken at once.</summary>
    public static Vector3 EasePosition(Vector3 current, Vector3 target, float dt, float halfLife, float snap)
    {
        if (!float.IsFinite(current.X) || !float.IsFinite(current.Y) || !float.IsFinite(current.Z) || Vector3.Distance(current, target) > snap)
            return target;
        var keep = halfLife > 0f && dt > 0f ? MathF.Pow(0.5f, dt / halfLife) : dt > 0f ? 0f : 1f;
        return target + (current - target) * keep;
    }
}

/// <summary>Why the minion glove is not in the world, or that it is.</summary>
public enum MinionWant
{
    /// <summary>Show it.</summary>
    Show,

    /// <summary>Not wanted just now (arrived, another zone, style changed): removed after a short linger.</summary>
    Soft,

    /// <summary>Must go now: zone change, logout, cutscene, group pose, hidden UI, no character, switched off.</summary>
    Hard,
}

/// <summary>
/// When the client-side glove object should exist. It is created when wanted, removed at once for a hard reason,
/// and kept for <see cref="Linger"/> seconds after a soft one so a flicker between walking and arriving does not
/// create and delete objects every frame. Failed creations back off (2, 4, 8 s) and stop after
/// <see cref="MaxAttempts"/> until <see cref="Reset"/>.
/// </summary>
public sealed class MinionPresence
{
    public const double Linger = 1.0;
    public const int MaxAttempts = 3;
    public const double LoadTimeout = 10;
    public const double RecoveryCooldown = 30;

    private double lastWanted = double.NegativeInfinity;
    private double nextTry = double.NegativeInfinity;
    private double loadingSince;
    public bool Loading { get; private set; }

    public int Failures { get; private set; }

    /// <summary>True once <see cref="MaxAttempts"/> creations have failed.</summary>
    public bool GaveUp => Failures >= MaxAttempts;

    /// <summary>Whether the object should exist now.</summary>
    public bool Keep(MinionWant want, double now)
    {
        switch (want)
        {
            case MinionWant.Show:
                lastWanted = now;
                return true;
            case MinionWant.Hard:
                lastWanted = double.NegativeInfinity;
                return false;
            default:
                return now - lastWanted < Linger;
        }
    }

    /// <summary>Whether a missing object may be created now.</summary>
    public bool MayCreate(double now) => !GaveUp && now >= nextTry;

    /// <summary>The default cursor retries a failed burst after a quiet cooldown; custom models retain their bounded attempts.</summary>
    public bool RecoverAfterCooldown(double now)
    {
        if (!GaveUp || !double.IsFinite(now) || now < nextTry + RecoveryCooldown) return false;
        Reset();
        return true;
    }

    public void Failed(double now)
    {
        Loading = false;
        Failures++;
        nextTry = now + 2.0 * Math.Pow(2, Math.Min(Failures - 1, 4));
    }

    /// <summary>Allocation starts an attempt; it does not prove the model loaded.</summary>
    public void BeginLoading(double now)
    {
        loadingSince = now;
        Loading = true;
    }

    /// <summary>Checks the initial model load, charging a timeout once. Readiness
    /// means loaded resources or actual visibility, never visibility alone being false.
    /// Once loaded, this actor may be hidden indefinitely without being recreated.</summary>
    public bool CheckLoadTimeout(bool resourcesReady, double now)
    {
        if (!Loading) return false;
        if (resourcesReady) { Created(); return false; }
        if (!double.IsFinite(now) || now - loadingSince <= LoadTimeout) return false;
        Failed(now);
        return true;
    }

    /// <summary>A successful model load, not merely an allocated actor.</summary>
    public void Created()
    {
        Loading = false;
        Failures = 0;
        nextTry = double.NegativeInfinity;
    }

    /// <summary>Starts over: after a settings change, or a new zone.</summary>
    public void Reset()
    {
        Failures = 0;
        Loading = false;
        nextTry = double.NegativeInfinity;
        lastWanted = double.NegativeInfinity;
    }
}
