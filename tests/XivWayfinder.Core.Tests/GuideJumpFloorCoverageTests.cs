using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuideJumpFloorCoverageTests
{
    private static GuideJumpPlan Plan(int direction = 1)
    {
        Vector3[] path = [Vector3.Zero, new(10, 0, 0)];
        var capability = new GuideJumpCapability(GuideJumpAnimationKind.ImportedClip, 0, "Jump", .8f, .12f, .5f);
        Assert.True(GuideJumpPolicy.TryRequest(path, direction > 0 ? 2 : 3.48f, direction, capability, out var request));
        return new(request!, request!.Start, request.End, .4f);
    }

    private static GuideJumpFloorTriangle[] Rectangle(float start, float end, float height = 0) =>
    [new(new(start, height, -1), new(end, height, -1), new(end, height, 1)),
        new(new(start, height, -1), new(end, height, 1), new(start, height, 1))];

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void RealSharedTriangleEdgesCoverTheWholeRouteInEitherDirection(int direction)
    {
        Assert.True(GuideJumpFloorCoverage.Covers(Plan(direction), Rectangle(0, 10)));
        GuideJumpFloorTriangle[] pieces = [.. Rectangle(1.9f, 2.5f), .. Rectangle(2.5f, 3), .. Rectangle(3, 3.6f)];
        Assert.True(GuideJumpFloorCoverage.Covers(Plan(direction), pieces.Reverse().ToArray()));
    }

    [Theory]
    [InlineData(.001f)]
    [InlineData(.01f)]
    [InlineData(.05f)]
    public void GapBetweenDownwardSamplePointsRemainsUnknown(float width)
    {
        // The old seventeen center rays could miss this gap entirely.
        GuideJumpFloorTriangle[] pieces = [.. Rectangle(1.9f, 2.72f), .. Rectangle(2.72f + width, 3.6f)];
        Assert.False(GuideJumpFloorCoverage.Covers(Plan(), pieces));
    }

    [Fact]
    public void DuplicateOrMissingHitTrianglesCannotInterpolateAcrossUnknownGround()
    {
        var piece = Rectangle(1.9f, 2.5f)[0];
        Assert.False(GuideJumpFloorCoverage.Covers(Plan(), Enumerable.Repeat(piece, 17).ToArray()));
        Assert.False(GuideJumpFloorCoverage.Covers(Plan(), []));
        Assert.False(GuideJumpFloorCoverage.Covers(Plan(), [default]));
    }

    [Fact]
    public void FloorOnAnotherLevelOrWallCannotFillTheMissingCoverage()
    {
        var plan = Plan();
        Assert.False(GuideJumpFloorCoverage.Covers(plan, Rectangle(0, 10, -1)));
        Assert.False(GuideJumpFloorCoverage.Covers(plan, Rectangle(0, 10, 2)));
        GuideJumpFloorTriangle wall = new(new(2, 0, -1), new(2, 2, -1), new(2, 2, 1));
        Assert.False(GuideJumpFloorCoverage.Covers(plan, [wall]));
        Assert.False(GuideJumpFloorCoverage.Covers(plan, [new(new(float.NaN), Vector3.Zero, Vector3.One)]));
    }

    [Fact]
    public void RaisedObstacleTopAndFloorMeetWithoutInventingAConnector()
    {
        GuideJumpFloorTriangle[] pieces = [.. Rectangle(1.9f, 2.6f), .. Rectangle(2.6f, 2.9f, .2f), .. Rectangle(2.9f, 3.6f)];
        Assert.True(GuideJumpFloorCoverage.Covers(Plan(), pieces));
    }

    [Fact]
    public void AHighTriangleEdgeBetweenSamplePointsCannotIntersectTheFootCurve()
    {
        GuideJumpFloorTriangle[] pieces = [.. Rectangle(1.9f, 3.6f), .. Rectangle(2.01f, 2.05f, .3f)];
        Assert.False(GuideJumpFloorCoverage.Covers(Plan(), pieces));
    }
}
