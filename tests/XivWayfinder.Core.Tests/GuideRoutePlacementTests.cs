using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuideRoutePlacementTests
{
    private static TrailSurfaceHit Floor(Vector3 point) => new(point, Vector3.UnitY, default, default, default);

    [Fact]
    public void ExactFloorThenTwoWallVetoRaysAreBoundedAndReturnUnshiftedFootRoot()
    {
        var queries = new List<(Vector3 From, Vector3 Direction, float Length)>();
        var target = new MinionPose(new(1, .2f, 0), .4f, 0);
        Assert.True(GuideRoutePlacement.TryPlace(target, 1, Vector3.Zero, (from, direction, length) =>
        { queries.Add((from, direction, length)); return queries.Count == 1 ? Floor(new(1, 0, 0)) : null; }, out var floor));
        Assert.Equal(new Vector3(1, 0, 0), floor); Assert.Equal(3, queries.Count);
        Assert.Equal(target.Position + Vector3.UnitY * 1.5f, queries[0].From);
        Assert.Equal(-Vector3.UnitY, queries[0].Direction); Assert.Equal(3, queries[0].Length);
        Assert.Equal(new Vector3(0, .3f, 0), queries[1].From); Assert.Equal(new Vector3(0, .8f, 0), queries[2].From);
    }

    [Fact]
    public void StationaryRootOnlyNeedsOneFloorRayAndNeverMovesToAnAdjacentHit()
    {
        var count = 0; var target = new MinionPose(new(1, 0, 2), 0, 0);
        Assert.True(GuideRoutePlacement.TryPlace(target, 1, target.Position, (_, _, _) => { count++; return Floor(target.Position); }, out _));
        Assert.Equal(1, count);
        Assert.False(GuideRoutePlacement.TryPlace(target, 1, null, (_, _, _) => Floor(target.Position + new Vector3(.03f, 0, 0)), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MissingSteepOtherStoreyAndMalformedFloorRejectBeforeAnyWallProbe(int failure)
    {
        var count = 0;
        Assert.False(GuideRoutePlacement.TryPlace(new(Vector3.Zero, 0, 0), 1, Vector3.UnitX, (_, _, _) =>
        {
            count++;
            return failure switch
            {
                0 => null,
                1 => new TrailSurfaceHit(Vector3.Zero, Vector3.UnitX, default, default, default),
                2 => Floor(new(0, 1.26f, 0)),
                _ => Floor(new(float.NaN))
            };
        }, out _));
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void EitherInterveningWallVetoesTheProposedMovement(int blockedQuery)
    {
        var count = 0;
        Assert.False(GuideRoutePlacement.TryPlace(new(Vector3.UnitX, 0, 0), 1, Vector3.Zero, (_, _, _) =>
        {
            count++;
            return count == 1 ? Floor(Vector3.UnitX) : count == blockedQuery
                ? new TrailSurfaceHit(new(.5f, .5f, 0), Vector3.UnitX, default, default, default) : null;
        }, out _));
        Assert.Equal(blockedQuery, count);
    }

    [Fact]
    public void InvalidPoseScaleOrOversizeJumpNeverStartsQueries()
    {
        var count = 0;
        TrailSurfaceHit? Query(Vector3 a, Vector3 b, float c) { count++; return null; }
        Assert.False(GuideRoutePlacement.TryPlace(new(new(float.NaN), 0, 0), 1, null, Query, out _));
        Assert.False(GuideRoutePlacement.TryPlace(new(Vector3.Zero, float.NaN, 0), 1, null, Query, out _));
        Assert.False(GuideRoutePlacement.TryPlace(new(Vector3.Zero, 0, 0), 0, null, Query, out _));
        Assert.False(GuideRoutePlacement.TryPlace(new(Vector3.Zero, 0, 0), 1, new(float.NaN), Query, out _));
        Assert.False(GuideRoutePlacement.TryPlace(new(new(9, 0, 0), 0, 0), 1, Vector3.Zero, Query, out _));
        Assert.Equal(0, count);
    }

    [Fact]
    public void SeatedRootRejectsTranslationAndFacingChangesButAcceptsEquivalentWrappedYaw()
    {
        var accepted = new MinionPose(new(1, 2, 3), .4f, 0);
        Assert.True(GuideRoutePlacement.SameHeldPose(accepted, accepted));
        Assert.True(GuideRoutePlacement.SameHeldPose(accepted with { Yaw = accepted.Yaw + MathF.Tau }, accepted));
        Assert.False(GuideRoutePlacement.SameHeldPose(accepted with { Position = accepted.Position + new Vector3(.01f, 0, 0) }, accepted));
        Assert.False(GuideRoutePlacement.SameHeldPose(accepted with { Yaw = .5f }, accepted));
        Assert.False(GuideRoutePlacement.SameHeldPose(accepted with { Pitch = float.NaN }, accepted));
    }
}
