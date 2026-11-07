using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class TrailGeometryTests
{
    [Fact]
    public void ProjectedGroundCoreReportsForeshorteningAgainstDirectProjection()
    {
        var eye = new Vector3(0, 6, 20); var viewport = new Vector2(1920, 1080);
        var vp = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, viewport.X / viewport.Y, .1f, 100);
        const float coreRadius = .12f * .16f;
        Assert.True(TrailGeometry.TryProjectedDisc(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, coreRadius,
            vp, viewport, out var center, out var groundPixels));
        var minimum = new Vector2(float.PositiveInfinity); var maximum = new Vector2(float.NegativeInfinity);
        for (var i = 0; i < 720; i++)
        {
            var angle = i * MathF.Tau / 720;
            var p = Vector4.Transform(new Vector4(MathF.Cos(angle) * coreRadius, 0, MathF.Sin(angle) * coreRadius, 1), vp);
            var screen = new Vector2((p.X / p.W + 1) * viewport.X / 2, (1 - p.Y / p.W) * viewport.Y / 2);
            minimum = Vector2.Min(minimum, screen); maximum = Vector2.Max(maximum, screen);
        }
        Assert.InRange(Vector2.Distance(center, viewport / 2), 0, .001f);
        Assert.InRange(Vector2.Distance(groundPixels, maximum - minimum), 0, .001f);
        Assert.InRange(groundPixels.Y, .1f, 1f); // A bright ground core is subpixel along its narrow axis.
        Assert.True(TrailGeometry.TryFrame(Vector3.Zero, eye, default, out var right, out var up, out _));
        Assert.True(TrailGeometry.TryProjectedDisc(Vector3.Zero, right, up, coreRadius, vp, viewport, out _, out var floatingPixels));
        Assert.True(floatingPixels.Y > 1);
    }

    [Fact]
    public void ProjectionDiagnosticsDistinguishBehindCameraFromOutsideViewport()
    {
        var viewport = new Vector2(1920, 1080);
        var vp = Matrix4x4.CreateLookAt(new(0, 0, 10), Vector3.Zero, Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, viewport.X / viewport.Y, .1f, 100);
        Assert.False(TrailGeometry.TryProjectedDisc(new(0, 0, 20), Vector3.UnitX, Vector3.UnitY, .1f, vp, viewport, out _, out _));
        Assert.True(TrailGeometry.TryProjectedDisc(new(100, 0, 0), Vector3.UnitX, Vector3.UnitY, .1f, vp, viewport, out var screen, out _));
        Assert.True(screen.X > viewport.X);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1.8f, .4f)]
    [InlineData(-.7f, 1.3f)]
    public void EntireGroundDiscStaysAboveItsMeasuredSlopingPlane(float slopeX, float slopeZ)
    {
        var ground = new Vector3(7, 3, -2);
        var normal = Vector3.Normalize(new Vector3(-slopeX, 1, -slopeZ));
        var center = ground + Vector3.UnitY * TrailPlacement.GroundClearance;
        Assert.True(TrailGeometry.TryFrame(center, new(0, 8, 12), normal, out var right, out var up, out _));
        for (var i = 0; i < 72; i++)
        {
            var angle = i * MathF.Tau / 72;
            var point = center + (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * TrailGeometry.MaximumRadius;
            Assert.True(Vector3.Dot(point - ground, normal) > .04f);
        }
    }

    [Fact]
    public void GroundRibbonWidthFollowsTheFloorRatherThanExtendingBelowIt()
    {
        var normal = Vector3.Normalize(new Vector3(-1.5f, 1, .6f));
        var a = new Vector3(0, TrailPlacement.GroundClearance, 0);
        var b = new Vector3(1, 1.5f + TrailPlacement.GroundClearance, 0);
        Assert.True(TrailGeometry.TryRibbonSide(a, b - a, new(0, 4, 7), normal, out var sideA));
        Assert.True(TrailGeometry.TryRibbonSide(b, b - a, new(0, 4, 7), normal, out var sideB));
        foreach (var point in new[] { a - sideA * .6f, a + sideA * .6f, b - sideB * .6f, b + sideB * .6f })
            Assert.True(Vector3.Dot(point, normal) > .07f);
    }

    [Theory]
    [InlineData(0, 1, 10)]
    [InlineData(2, 20, 1)]
    [InlineData(0, 3, 0)]
    public void FloatingGlowEntireExtentStaysAboveTheRequestedAltitude(float eyeX, float eyeY, float eyeZ)
    {
        var at = new Vector3(0, 2.5f, 0);
        Assert.True(TrailGeometry.TryFrame(at, new(eyeX, eyeY, eyeZ), default, out var right, out var up, out _));
        var center = TrailGeometry.AboveAltitude(at, right, up, .6f);
        for (var i = 0; i < 72; i++)
        {
            var angle = i * MathF.Tau / 72;
            var point = center + (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * .6f;
            Assert.True(point.Y >= at.Y - .00001f);
        }
    }

    [Fact]
    public void FloatingRibbonGlowKeepsBothEdgesAboveTheAltitude()
    {
        var a = new Vector3(0, 3, 0);
        var b = new Vector3(.4f, 3.7f, 1);
        foreach (var point in new[] { a, b })
        {
            Assert.True(TrailGeometry.TryRibbonSide(point, b - a, new(3, 6, 5), default, out var side));
            var center = TrailGeometry.AboveAltitude(point, side, default, .6f);
            Assert.True((center + side * .6f).Y >= point.Y - .00001f);
            Assert.True((center - side * .6f).Y >= point.Y - .00001f);
        }
    }

    [Fact]
    public void PixelRadiusStaysConsistentAcrossPerspectiveDepth()
    {
        var viewport = new Vector2(1920, 1080);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, viewport.X / viewport.Y, .1f, 200);
        foreach (var depth in new[] { 2f, 8f, 30f })
        {
            var point = new Vector3(0, 0, -depth);
            var radius = TrailGeometry.RadiusForPixels(point, Vector3.UnitX, 8, projection, viewport);
            var a = Project(point, projection, viewport);
            var b = Project(point + Vector3.UnitX * radius, projection, viewport);
            Assert.InRange(Vector2.Distance(a, b), 7.999f, 8.001f);
        }
        Assert.Equal(TrailGeometry.MaximumRadius,
            TrailGeometry.RadiusForPixels(new(0, 0, -100), Vector3.UnitX, 100, projection, viewport));
    }

    [Fact]
    public void DegenerateOrNonFiniteGeometryCannotCreateFullscreenMarks()
    {
        Assert.False(TrailGeometry.TryFrame(Vector3.Zero, Vector3.Zero, default, out _, out _, out _));
        Assert.False(TrailGeometry.TryRibbonSide(Vector3.Zero, Vector3.Zero, Vector3.One, default, out _));
        Assert.False(TrailGeometry.TryFrame(new(float.NaN, 0, 0), Vector3.One, default, out _, out _, out _));
        Assert.Equal(0, TrailGeometry.RadiusForPixels(Vector3.Zero, Vector3.UnitX, 8, default, new(1920, 1080)));
        Assert.Equal(0, TrailGeometry.RadiusForPixels(Vector3.Zero, Vector3.UnitX, float.NaN, Matrix4x4.Identity, new(1920, 1080)));
    }

    private static Vector2 Project(Vector3 point, Matrix4x4 matrix, Vector2 viewport)
    {
        var clip = Vector4.Transform(new Vector4(point, 1), matrix);
        return new Vector2(clip.X, clip.Y) / clip.W * viewport * .5f;
    }
}
