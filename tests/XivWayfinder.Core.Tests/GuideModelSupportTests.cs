using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuideModelSupportTests
{
    private static readonly int[] Triangle = [0, 1, 2];
    private static TrailSurfaceHit Plane(float slope = 0, float offset = 0) => new(
        new(offset, 0, offset), Vector3.Zero,
        new(offset - 5, -5 * slope, offset - 5),
        new(offset + 5, 5 * slope, offset - 5),
        new(offset, 0, offset + 5));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UphillFootprintLiftsOnlyEnoughToClearTheConfirmedPlane(bool reverse)
    {
        var support = Plane(.5f);
        if (reverse) support = support with { B = support.C, C = support.B };
        Vector3[] body = [new(-1, .03f, 0), new(1, .03f, 0), new(0, 1, 1)];
        Assert.True(GuideModelSupport.TryGetLift(body, Triangle, support, .03f, out var lift));
        Assert.Equal(.5f, lift, 6);
        foreach (var vertex in body) Assert.True(vertex.Y + lift >= vertex.X * .5f + .03f - .000001f);
        Assert.Equal(.03f, body[1].Y + lift - body[1].X * .5f, 5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.2f)]
    [InlineData(1.5f)]
    public void FlatFloorNeverCancelsExistingAnimatedLift(float bounce)
    {
        Vector3[] body = [new(-1, .03f + bounce, 0), new(1, .03f + bounce, 0), new(0, 1 + bounce, 1)];
        Assert.True(GuideModelSupport.TryGetLift(body, Triangle, Plane(), .03f, out var lift));
        Assert.Equal(0, lift);
    }

    [Fact]
    public void UphillAlreadyAirbornePoseIsNotLoweredOntoThePlane()
    {
        Vector3[] body = [new(-1, 1, 0), new(1, 1, 0), new(0, 2, 1)];
        Assert.True(GuideModelSupport.TryGetLift(body, Triangle, Plane(.5f), .03f, out var lift));
        Assert.Equal(0, lift);
    }

    [Fact]
    public void PartialCoverageCannotExtrapolateAConfirmedPlane()
    {
        Vector3[] body = [new(-1, .03f, 0), new(1, .03f, 0), new(6, .03f, 0)];
        Assert.False(GuideModelSupport.TryGetLift(body, Triangle, Plane(.5f), .03f, out var lift));
        Assert.Equal(0, lift); // Earlier uphill vertices must not leak a partial correction.
    }

    [Fact]
    public void UnreferencedVerticesCannotChangeVisibleSupport()
    {
        Vector3[] body = [new(-1, .03f, 0), new(1, .03f, 0), new(0, 1, 1), new(100, -100, 100)];
        Assert.True(GuideModelSupport.TryGetLift(body, Triangle, Plane(.5f), .03f, out var lift));
        Assert.Equal(.5f, lift, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(-1000)]
    public void ExactTriangleSeamsAndWorldOffsetsKeepTheSameSupport(float offset)
    {
        var support = Plane(.25f, offset);
        Vector3[] body = [support.A with { Y = .03f }, support.B with { Y = .03f }, support.C with { Y = 1 }];
        Assert.True(GuideModelSupport.TryGetLift(body, Triangle, support, .03f, out var lift));
        Assert.Equal(1.25f, lift, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void MalformedWallOrInconsistentCollisionEvidenceCannotLiftAModel(int failure)
    {
        var support = Plane(.5f);
        support = failure switch
        {
            0 => support with { A = support.B, C = support.B },
            1 => Plane(4),
            2 => support with { Point = new(0, .5f, 0) },
            3 => support with { Point = new(20, 0, 0) },
            4 => support with { A = new(float.NaN) },
            _ => support with { C = new(float.PositiveInfinity) }
        };
        Vector3[] body = [new(-1, .03f, 0), new(1, .03f, 0), new(0, 1, 1)];
        Assert.False(GuideModelSupport.TryGetLift(body, Triangle, support, .03f, out var lift));
        Assert.Equal(0, lift);
    }

    [Fact]
    public void ActualRotatedAndScaledAnimatedVerticesDetermineUphillClearance()
    {
        Vector3[] posed = [new(0, 0, -.5f), new(0, .1f, .5f), new(0, .8f, 0)];
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var world = posed.Select(p => Vector3.Transform(p * 2, rotation) + new Vector3(0, .03f, 0)).ToArray();
        Assert.True(GuideModelSupport.TryGetLift(world, Triangle, Plane(.5f), .03f, out var lift));
        Assert.Equal(.3f, lift, 5);
    }
}
