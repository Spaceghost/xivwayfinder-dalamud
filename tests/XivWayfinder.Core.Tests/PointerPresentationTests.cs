using System.Numerics;
namespace XivWayfinder.Core.Tests;
public sealed class PointerPresentationTests
{
    [Fact]
    public void CompanionExistsWithoutAnyDestinationOrRoute()
    {
        var pose = PointerPresentation.Companion(new(5, 10, 8), 0, Vector2.Zero,
            new(1.6f, 0, 2.4f, 0, 35), 1, true)!.Value;
        Assert.Equal(new Vector3(5, 12.4f, 9.6f), pose.Position);
        Assert.Equal(0, pose.Yaw);
    }
    [Fact]
    public void DestinationIsDownwardAndSlowlyBouncesVertically()
    {
        var at = new Vector3(8, 3, 6);
        var first = PointerPresentation.Destination(at, true, PointerPresentation.Period / 4, false)!.Value;
        var later = PointerPresentation.Destination(at, true, PointerPresentation.Period * 3 / 4, false)!.Value;
        Assert.Equal(at.X, first.Position.X); Assert.Equal(at.Z, later.Position.Z);
        Assert.InRange(Math.Abs(later.Position.Y - first.Position.Y), .35f, .37f);
        Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitZ,
            MinionGlove.Orientation(first.Yaw, first.Pitch)), -Vector3.UnitY), 0, .0001f);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReducedMotionKeepsBothRolesStill(bool destination)
    {
        MinionPose? Pose(double t) => destination ? PointerPresentation.Destination(Vector3.Zero, true, t, true)
            : PointerPresentation.Companion(Vector3.Zero, 1, Vector2.UnitX, MinionLayout.Default, t, true);
        Assert.Equal(Pose(0), Pose(1.8));
    }
    [Fact]
    public void MarkerClearsImmediatelyWithoutVisibleKnownDestination()
    {
        Assert.Null(PointerPresentation.Destination(null, true, 0, false));
        Assert.Null(PointerPresentation.Destination(Vector3.Zero, false, 0, false));
        Assert.Null(PointerPresentation.Destination(new(float.NaN), true, 0, false));
        Assert.Null(PointerPresentation.Companion(Vector3.Zero, float.NaN, Vector2.Zero, MinionLayout.Default, 0, false));
    }
    [Fact]
    public void DirectionChangesFingerYawWithoutMovingItAwayFromPlayerHeading()
    {
        var a = PointerPresentation.Companion(Vector3.Zero, MathF.PI / 2, Vector2.UnitY, MinionLayout.Default, 1, false)!.Value;
        var b = PointerPresentation.Companion(Vector3.Zero, MathF.PI / 2, -Vector2.UnitY, MinionLayout.Default, 1, false)!.Value;
        Assert.Equal(a.Position, b.Position);Assert.NotEqual(a.Yaw, b.Yaw);
    }
}
