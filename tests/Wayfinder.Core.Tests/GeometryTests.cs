using System.Numerics;

namespace Wayfinder.Core.Tests;

/// <summary>Direction and angle maths on the ground plane, and the map conversions.</summary>
public sealed class GeometryTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void RotationZeroFacesPlusZ()
    {
        var f = Heading.Forward(0f);
        Assert.Equal(0f, f.X, Eps);
        Assert.Equal(1f, f.Y, Eps);
        var right = Heading.Forward(MathF.PI / 2);
        Assert.Equal(1f, right.X, Eps);
        Assert.Equal(0f, right.Y, Eps);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.7f)]
    [InlineData(-2.5f)]
    [InlineData(3.1f)]
    public void RotationOfInvertsForward(float r) => Assert.Equal(r, Heading.RotationOf(Heading.Forward(r)), Eps);

    [Fact]
    public void DirectionIgnoresHeightAndIsUnitLength()
    {
        var d = Heading.DirectionTo(new Vector3(10, 0, 10), new Vector3(13, 50, 14));
        Assert.Equal(0.6f, d.X, Eps);
        Assert.Equal(0.8f, d.Y, Eps);
        Assert.Equal(5f, Heading.FlatDistance(new Vector3(10, 0, 10), new Vector3(13, -80, 14)), Eps);
    }

    [Fact]
    public void DirectionToTheSameSpotIsZeroNotNaN()
    {
        Assert.Equal(Vector2.Zero, Heading.DirectionTo(new Vector3(1, 2, 3), new Vector3(1, 9, 3)));
        Assert.Equal(Vector2.Zero, Heading.DirectionTo(Vector3.Zero, new Vector3(float.NaN, 0, 0)));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(4f, 4f - 2 * MathF.PI)]
    [InlineData(-4f, -4f + 2 * MathF.PI)]
    [InlineData(5f * MathF.PI / 2, MathF.PI / 2)]
    [InlineData(-MathF.PI, MathF.PI)]
    public void WrapFoldsIntoMinusPiToPi(float angle, float expected) => Assert.Equal(expected, Heading.Wrap(angle), 1e-3f);

    [Fact]
    public void BetweenTakesTheShortWayRound()
    {
        Assert.Equal(2 * MathF.PI - 6f, Heading.Between(3f, -3f), 1e-3f);
        Assert.Equal(MathF.PI, Heading.Between(0f, MathF.PI), Eps);
    }

    [Fact]
    public void TurnAwayIsZeroFacingHalfSidewaysOneBehind()
    {
        var dir = Heading.Forward(1.2f);
        Assert.Equal(0f, Heading.TurnAway(1.2f, dir), Eps);
        Assert.Equal(0.5f, Heading.TurnAway(1.2f + MathF.PI / 2, dir), Eps);
        Assert.Equal(0.5f, Heading.TurnAway(1.2f - MathF.PI / 2, dir), Eps);
        Assert.Equal(1f, Heading.TurnAway(1.2f + MathF.PI, dir), Eps);
        Assert.Equal(0f, Heading.TurnAway(1.2f, Vector2.Zero));
    }

    [Fact]
    public void AheadGoesAlongTheGroundAndUp()
    {
        var p = Heading.Ahead(new Vector3(100, 20, -50), new Vector2(0, 1), 2.5f, 1.2f);
        Assert.Equal(new Vector3(100, 21.2f, -47.5f), p);
    }

    [Fact]
    public void TestTargetIsTwentyYalmsStraightAhead()
    {
        // what /wayfinder test does with the character's position and rotation
        var at = new Vector3(-12, 4, 30);
        var rotation = -2.1f;
        var target = Heading.Ahead(at, Heading.Forward(rotation), 20f, 0f);
        Assert.Equal(20f, Heading.FlatDistance(at, target), 1e-3f);
        Assert.Equal(0f, Heading.TurnAway(rotation, Heading.DirectionTo(at, target)), 1e-4f);
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(100, -300)]
    [InlineData(200, 50)]
    [InlineData(400, 0)]
    public void MapCoordinatesRoundTrip(int size, int offset)
    {
        foreach (var world in new[] { -1000f, -512f, 0f, 3.7f, 640f })
        {
            var map = MapCoords.WorldToMap(world, (ushort)size, (short)offset);
            Assert.Equal(world, MapCoords.MapToWorld(map, (ushort)size, (short)offset), 0.01f);
        }
    }

    [Fact]
    public void MapCoordinatesMatchTheGamesScale()
    {
        // a size-100 map: the world origin is 21.48, the edges 1 and about 42
        Assert.Equal(21.48f, MapCoords.WorldToMap(0f, 100, 0), 1e-3f);
        Assert.Equal(1f, MapCoords.WorldToMap(-1024f, 100, 0), 1e-3f);
        Assert.Equal(41.96f, MapCoords.WorldToMap(1024f, 100, 0), 1e-3f);
        // a size-200 city map spans half the world distance for the same numbers
        Assert.Equal(11.24f, MapCoords.WorldToMap(0f, 200, 0), 1e-3f);
    }

    [Fact]
    public void MapMarkerPixelsAgreeWithMapCoordinates()
    {
        // pixel p of the 2048-pixel map texture is map coordinate p / (size / 2) + 1, through world space
        foreach (var (size, offset) in new[] { (100, 0), (200, -448), (100, 64) })
        {
            foreach (var pixel in new[] { 0f, 512f, 1024f, 1700f, 2048f })
            {
                var world = MapCoords.PixelToWorld(pixel, (ushort)size, (short)offset);
                var map = MapCoords.WorldToMap(world, (ushort)size, (short)offset);
                Assert.Equal(pixel / (size / 2f) + 1f, map, 1e-3f);
            }
        }
    }

    [Theory]
    [InlineData(11.2f, true)]
    [InlineData(41.9f, true)]
    [InlineData(0f, false)]
    [InlineData(-3f, false)]
    [InlineData(45f, false)]
    [InlineData(float.NaN, false)]
    public void PlausibleMapCoordinates(float value, bool ok) => Assert.Equal(ok, MapCoords.IsPlausibleMapCoord(value));
}
