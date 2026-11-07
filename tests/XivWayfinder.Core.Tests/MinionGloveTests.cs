using System.Numerics;

namespace XivWayfinder.Core.Tests;

/// <summary>The minion glove's placement, turning, tilt, easing, and when its client-side object should exist.</summary>
public sealed class MinionGloveTests
{
    private static readonly Vector3 Player = new(100f, 20f, -50f);

    [Theory]
    [InlineData("Wind-up Cursor")]
    [InlineData("wind-up cursor")]
    [InlineData(" WIND-UP CURSOR ")]
    public void FindsTheMinionByName(string name) => Assert.True(MinionGlove.IsWindUpCursor(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wind-up cursors")]
    [InlineData("cursor")]
    [InlineData("Wind-up Airship")]
    public void OtherMinionsAreNotIt(string? name) => Assert.False(MinionGlove.IsWindUpCursor(name));

    [Fact]
    public void RightIsWestWhenFacingSouth()
    {
        // X east, Z south: facing +Z the right hand is −X; facing east (+X) it is south (+Z)
        Assert.Equal(new Vector2(-1f, 0f), MinionGlove.Right(new Vector2(0f, 1f)));
        Assert.Equal(new Vector2(0f, 1f), MinionGlove.Right(new Vector2(1f, 0f)));
        // and it agrees with the character convention: 90° clockwise seen from above is rotation − π/2
        var dir = Heading.Forward(0.7f);
        var right = Heading.Forward(0.7f - MathF.PI / 2f);
        Assert.Equal(right.X, MinionGlove.Right(dir).X, 1e-5f);
        Assert.Equal(right.Y, MinionGlove.Right(dir).Y, 1e-5f);
    }

    [Fact]
    public void FloatsAheadAndBesideAtRest()
    {
        var layout = new MinionLayout(1.6f, 0.7f, 0.5f, 0f, 35f);
        // t = 0: the tap is at rest and the bob at its midpoint
        var at = MinionGlove.Place(Player, new Vector2(0f, 1f), layout, 0)!.Value;
        Assert.Equal(Player.X - 0.7f, at.X, 1e-4f);
        Assert.Equal(Player.Z + 1.6f, at.Z, 1e-4f);
        Assert.Equal(Player.Y + 0.5f, at.Y, 1e-4f);
    }

    [Fact]
    public void StaysCloseWhateverTheTime()
    {
        var layout = MinionLayout.Default;
        var dir = Vector2.Normalize(new Vector2(0.3f, -0.8f));
        for (var t = 0.0; t < 6; t += 0.013)
        {
            var at = MinionGlove.Place(Player, dir, layout, t)!.Value;
            var flat = Heading.Flat(Player, at);
            var along = Vector2.Dot(flat, dir);
            var side = Vector2.Dot(flat, MinionGlove.Right(dir));
            Assert.InRange(along, layout.Ahead - 1e-4f, layout.Ahead + 0.18f + 1e-4f);
            Assert.Equal(layout.Side, side, 1e-3f);
            Assert.InRange(at.Y - Player.Y, layout.Height - 0.08f - 1e-4f, layout.Height + 0.08f + 1e-4f);
        }
    }

    [Fact]
    public void AnUnnormalisedDirectionPlacesTheSame()
    {
        var layout = MinionLayout.Default;
        var a = MinionGlove.Place(Player, new Vector2(3f, 4f), layout, 0.4)!.Value;
        var b = MinionGlove.Place(Player, new Vector2(0.6f, 0.8f), layout, 0.4)!.Value;
        Assert.Equal(a.X, b.X, 1e-4f);
        Assert.Equal(a.Z, b.Z, 1e-4f);
    }

    [Fact]
    public void NoDirectionNoPlace()
    {
        Assert.Null(MinionGlove.Place(Player, Vector2.Zero, MinionLayout.Default, 1));
        Assert.Null(MinionGlove.Place(Player, new Vector2(float.NaN, 1f), MinionLayout.Default, 1));
    }

    [Theory]
    [InlineData(0f, -2f, 0f)]
    [InlineData(90f, 0f, 2f)]
    [InlineData(180f, 2f, 0f)]
    [InlineData(-90f, 0f, -2f)]
    public void BesideGuideStaysAtThePlayersShoulderAsThePlayerTurns(float degrees, float x, float z)
    {
        var layout = MinionLayout.BesideDefault with { Height = 0 };
        var rotation = degrees * MathF.PI / 180;
        // Across animation phases: no old forward tap, no below-ground bob, always two yalms to the right.
        for (var t = 0.0; t < 3; t += .037)
        {
            var at = MinionGlove.PlaceBeside(Player, rotation, layout, t)!.Value;
            Assert.Equal(Player.X + x, at.X, .0001f);
            Assert.Equal(Player.Z + z, at.Z, .0001f);
            Assert.Equal(Player.Y, at.Y);
            Assert.Equal(0, Vector2.Dot(Heading.Flat(Player, at), Heading.Forward(rotation)), .0001f);
        }
    }

    [Fact]
    public void BesideGuideCanStayLeftOrAheadOfTheShoulderWithoutDrifting()
    {
        const float rotation = .73f;
        var forward = Heading.Forward(rotation); var right = MinionGlove.Right(forward);
        var layout = MinionLayout.BesideDefault with { Ahead = .6f, Side = -1.5f, Height = 2f };
        for (var t = 0.0; t < 3; t += .037)
        {
            var at = MinionGlove.PlaceBeside(Player, rotation, layout, t)!.Value;
            Assert.Equal(.6f, Vector2.Dot(Heading.Flat(Player, at), forward), .0001f);
            Assert.Equal(-1.5f, Vector2.Dot(Heading.Flat(Player, at), right), .0001f);
            Assert.InRange(at.Y - Player.Y, 1.9199f, 2.0801f);
        }
    }

    [Fact]
    public void BesidePlacementClampsCorruptOffsetsAndRejectsInvalidPlayerPoses()
    {
        Assert.Equal(0, MinionLayout.CompanionAhead(0));
        Assert.Equal(0, MinionLayout.CompanionAhead(-100));
        Assert.Equal(3, MinionLayout.CompanionAhead(100));
        Assert.Equal(0, MinionLayout.CompanionAhead(float.NaN));
        Assert.Equal(2, MinionLayout.CompanionSide(float.PositiveInfinity));
        Assert.Equal(-2, MinionLayout.CompanionSide(-100));
        var at = MinionGlove.PlaceBeside(Player, 0, new(float.NaN, float.NaN, float.NaN, 0, 0), double.NaN)!.Value;
        Assert.Equal(Player + new Vector3(-2, .5f, 0), at);
        Assert.Null(MinionGlove.PlaceBeside(Player, float.NaN, MinionLayout.BesideDefault, 0));
        Assert.Null(MinionGlove.PlaceBeside(new(float.PositiveInfinity, 0, 0), 0, MinionLayout.BesideDefault, 0));
    }

    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(1f, 0f, 90f)]
    [InlineData(0f, -1f, 180f)]
    [InlineData(-1f, 0f, -90f)]
    public void YawFacesTheWay(float x, float z, float degrees)
    {
        var yaw = MinionGlove.Yaw(new Vector2(x, z), 0f);
        Assert.Equal(0f, Heading.Wrap(yaw - degrees * MathF.PI / 180f), 1e-4f);
        // and the character convention agrees: that rotation faces the same way
        var f = Heading.Forward(yaw);
        Assert.Equal(x, f.X, 1e-4f);
        Assert.Equal(z, f.Y, 1e-4f);
    }

    [Fact]
    public void TurnCorrectsAModelThatFacesAnotherWay()
    {
        var yaw = MinionGlove.Yaw(new Vector2(0f, 1f), 90f);
        Assert.Equal(MathF.PI / 2f, yaw, 1e-4f);
        Assert.Equal(-MathF.PI / 2f, MinionGlove.Yaw(new Vector2(0f, 1f), 270f), 1e-4f);
        Assert.Equal(0f, MinionGlove.Yaw(new Vector2(0f, 1f), float.NaN), 1e-4f);
    }

    [Fact]
    public void PitchesUpAndDownSlopesWithinItsLimit()
    {
        var from = new Vector3(0f, 0f, 0f);
        Assert.Equal(MathF.PI / 4f, MinionGlove.Pitch(from, new Vector3(0f, 10f, 10f), true, 60f), 1e-4f);
        Assert.Equal(-MathF.PI / 4f, MinionGlove.Pitch(from, new Vector3(10f, -10f, 0f), true, 60f), 1e-4f);
        // clamped
        Assert.Equal(35f * MathF.PI / 180f, MinionGlove.Pitch(from, new Vector3(0f, 50f, 5f), true, 35f), 1e-4f);
        Assert.Equal(-35f * MathF.PI / 180f, MinionGlove.Pitch(from, new Vector3(0f, -50f, 5f), true, 35f), 1e-4f);
        // level: height unknown, switched off, straight overhead, nonsense
        Assert.Equal(0f, MinionGlove.Pitch(from, new Vector3(0f, 10f, 10f), false, 35f));
        Assert.Equal(0f, MinionGlove.Pitch(from, new Vector3(0f, 10f, 10f), true, 0f));
        Assert.Equal(0f, MinionGlove.Pitch(from, new Vector3(0.1f, 10f, 0.1f), true, 35f));
        Assert.Equal(0f, MinionGlove.Pitch(from, new Vector3(float.NaN, 1f, 3f), true, 35f));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.9f, 0f)]
    [InlineData(-2.4f, 0.3f)]
    [InlineData(3.1f, -0.5f)]
    [InlineData(1.2f, 0.61f)]
    public void OrientationPointsTheModelsForwardWhereAsked(float yaw, float pitch)
    {
        var q = MinionGlove.Orientation(yaw, pitch);
        var forward = Vector3.Transform(Vector3.UnitZ, q);
        var (sy, cy) = MathF.SinCos(yaw);
        var (sp, cp) = MathF.SinCos(pitch);
        Assert.Equal(sy * cp, forward.X, 1e-4f);
        Assert.Equal(sp, forward.Y, 1e-4f);
        Assert.Equal(cy * cp, forward.Z, 1e-4f);
        // no roll: the model's right hand stays level
        var right = Vector3.Transform(Vector3.UnitX, q);
        Assert.Equal(0f, right.Y, 1e-4f);
        Assert.Equal(1f, q.Length(), 1e-4f);
    }

    [Fact]
    public void OrientationWithoutPitchIsJustTheYaw()
    {
        var q = MinionGlove.Orientation(1.1f, 0f);
        var yawOnly = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.1f);
        Assert.True(MathF.Abs(Quaternion.Dot(q, yawOnly)) > 1f - 1e-5f);
        Assert.Equal(Quaternion.Identity, MinionGlove.Orientation(float.NaN, float.NaN));
    }

    [Fact]
    public void EasingAnAngleTakesTheShortWayRound()
    {
        // from 170° to −170° is 20° through 180°, not 340° back through 0
        var from = 170f * MathF.PI / 180f;
        var to = -170f * MathF.PI / 180f;
        var half = MinionGlove.EaseAngle(from, to, 0.1f, 0.1f);
        Assert.Equal(180f * MathF.PI / 180f, MathF.Abs(half), 1e-3f);
        // converges, whatever the frame rate
        var a = 0f;
        for (var i = 0; i < 200; i++)
            a = MinionGlove.EaseAngle(a, 2f, 1f / 60f, 0.1f);
        Assert.Equal(2f, a, 1e-3f);
        Assert.Equal(1f, MinionGlove.EaseAngle(float.NaN, 1f, 0.016f, 0.1f));
        Assert.Equal(0.5f, MinionGlove.EaseAngle(0.5f, 1f, 0f, 0.1f));
    }

    [Fact]
    public void EasingAPositionSnapsAcrossATeleport()
    {
        var a = new Vector3(0f, 0f, 0f);
        var b = new Vector3(1f, 0f, 0f);
        Assert.Equal(0.5f, MinionGlove.EasePosition(a, b, 0.1f, 0.1f, 10f).X, 1e-4f);
        Assert.Equal(new Vector3(500f, 0f, 0f), MinionGlove.EasePosition(a, new Vector3(500f, 0f, 0f), 0.016f, 0.1f, 10f));
        Assert.Equal(b, MinionGlove.EasePosition(new Vector3(float.NaN), b, 0.016f, 0.1f, 10f));
        Assert.Equal(a, MinionGlove.EasePosition(a, b, 0f, 0.1f, 10f));
    }

    [Fact]
    public void TheMeasuredRowsAreDocumented()
    {
        // what the game data of 2026.09.15 holds; the plugin matches by name, these only document it
        Assert.Equal(51u, MinionGlove.MeasuredCompanionRow);
        Assert.Equal(469u, MinionGlove.MeasuredModelChara);
    }
}

public sealed class MinionPresenceTests
{
    [Fact]
    public void ShownWhileWantedAndGoneAtOnceForAHardReason()
    {
        var p = new MinionPresence();
        Assert.True(p.Keep(MinionWant.Show, 10));
        Assert.False(p.Keep(MinionWant.Hard, 10.01));
        // a soft reason right after a hard one does not bring it back
        Assert.False(p.Keep(MinionWant.Soft, 10.02));
    }

    [Fact]
    public void LingersBrieflyForASoftReason()
    {
        var p = new MinionPresence();
        Assert.False(p.Keep(MinionWant.Soft, 0));
        Assert.True(p.Keep(MinionWant.Show, 5));
        Assert.True(p.Keep(MinionWant.Soft, 5.5));
        Assert.True(p.Keep(MinionWant.Soft, 5.99));
        Assert.False(p.Keep(MinionWant.Soft, 6.0));
    }

    [Fact]
    public void FailedCreationsBackOffAndThenStop()
    {
        var p = new MinionPresence();
        Assert.True(p.MayCreate(0));
        p.Failed(0);
        Assert.False(p.MayCreate(1.9));
        Assert.True(p.MayCreate(2.0));
        p.Failed(2);
        Assert.False(p.MayCreate(5.9));
        Assert.True(p.MayCreate(6.0));
        p.Failed(6);
        Assert.True(p.GaveUp);
        Assert.False(p.MayCreate(1000));
        p.Reset();
        Assert.True(p.MayCreate(1000));
        Assert.False(p.GaveUp);
    }

    [Fact]
    public void ASuccessClearsTheFailures()
    {
        var p = new MinionPresence();
        p.Failed(0);
        p.Failed(2);
        p.Created();
        Assert.Equal(0, p.Failures);
    }

    [Fact]
    public void ThreeAllocatedActorsThatNeverLoadExhaustTheRetryBudget()
    {
        var p = new MinionPresence(); var now = 0d;
        for (var attempt = 0; attempt < MinionPresence.MaxAttempts; attempt++)
        {
            Assert.True(p.MayCreate(now));
            p.BeginLoading(now);
            Assert.Equal(attempt, p.Failures); // allocation cannot forgive past failures
            Assert.False(p.CheckLoadTimeout(false, now + MinionPresence.LoadTimeout));
            now += MinionPresence.LoadTimeout + .01;
            Assert.True(p.CheckLoadTimeout(false, now));
            Assert.Equal(attempt + 1, p.Failures);
            Assert.False(p.MayCreate(now + 1.99));
            now += 2 * Math.Pow(2, attempt);
        }
        Assert.True(p.GaveUp);
        Assert.False(p.MayCreate(now + 1000));
    }

    [Fact]
    public void EachLoadTimeoutIsChargedOnceEvenIfPolledAgain()
    {
        var p = new MinionPresence(); p.BeginLoading(5);
        Assert.True(p.CheckLoadTimeout(false, 16));
        Assert.False(p.Loading);
        Assert.False(p.CheckLoadTimeout(false, 17));
        Assert.False(p.CheckLoadTimeout(true, 18)); // a late observation cannot forgive the retired attempt
        Assert.Equal(1, p.Failures);
    }

    [Fact]
    public void LoadedButHiddenActorDoesNotTimeOutOrRestartLoading()
    {
        var p = new MinionPresence(); p.Failed(0); p.BeginLoading(2);
        // Native resource readiness is sufficient even when visibility is false.
        Assert.False(p.CheckLoadTimeout(true, 3));
        Assert.False(p.Loading); Assert.Equal(0, p.Failures);
        for (var now = 4; now < 1000; now++) Assert.False(p.CheckLoadTimeout(false, now));
        Assert.Equal(0, p.Failures); Assert.False(p.GaveUp);
    }

    [Fact]
    public void LateReadinessWinsAtDeadlineAndFreshAllocationGetsAFreshDeadline()
    {
        var p = new MinionPresence(); p.BeginLoading(0);
        Assert.False(p.CheckLoadTimeout(true, 11));
        p.BeginLoading(20);
        Assert.False(p.CheckLoadTimeout(false, 30));
        Assert.True(p.CheckLoadTimeout(false, 30.01));
        Assert.Equal(1, p.Failures);
        p.Reset(); Assert.False(p.Loading); Assert.True(p.MayCreate(31));
    }
    [Fact]
    public void DefaultCursorRecoversOnlyAfterTheExhaustedBurstCooldown()
    {
        var p = new MinionPresence();
        p.Failed(0); p.Failed(2); p.Failed(6);
        Assert.True(p.GaveUp);
        Assert.False(p.RecoverAfterCooldown(43.99));
        Assert.False(p.RecoverAfterCooldown(double.NaN));
        Assert.False(p.RecoverAfterCooldown(double.PositiveInfinity));
        Assert.True(p.RecoverAfterCooldown(44));
        Assert.False(p.GaveUp);
        Assert.True(p.MayCreate(44));
        p.BeginLoading(44);
        Assert.True(p.CheckLoadTimeout(false, 55));
        Assert.Equal(1, p.Failures);
    }

    [Fact]
    public void RecoveryDoesNotInterruptAnActiveLoadOrNormalBackoff()
    {
        var p = new MinionPresence();
        p.BeginLoading(0);
        Assert.False(p.RecoverAfterCooldown(1));
        Assert.True(p.Loading);
        p.Failed(11);
        Assert.False(p.RecoverAfterCooldown(12));
        Assert.False(p.MayCreate(12));
    }

}
