using System.Numerics;

namespace Wayfinder.Core.Tests;

/// <summary>Screen-edge clamping and the glove's placement, turning, mirroring and texture coordinates.</summary>
public sealed class ScreenAndGloveTests
{
    private static readonly Vector2 Min = new(40, 40);
    private static readonly Vector2 Max = new(1880, 1040);

    [Fact]
    public void RayFromTheCentreMeetsTheRightEdge()
    {
        var p = ScreenEdge.RayToEdge(new Vector2(960, 540), new Vector2(1, 0), Min, Max);
        Assert.Equal(new Vector2(1880, 540), p);
    }

    [Fact]
    public void DiagonalRayMeetsWhicheverEdgeComesFirst()
    {
        // up and to the right from the centre: 500 px to the top edge, 920 to the right one
        var p = ScreenEdge.RayToEdge(new Vector2(960, 540), new Vector2(1, -1), Min, Max);
        Assert.Equal(40f, p.Y, 1e-3f);
        Assert.Equal(1460f, p.X, 1e-3f);
    }

    [Fact]
    public void BehindTheCameraMeansTheBottomEdge()
    {
        // a zero direction (looking straight down it) points towards the camera: down
        var p = ScreenEdge.RayToEdge(new Vector2(960, 540), Vector2.Zero, Min, Max);
        Assert.Equal(new Vector2(960, 1040), p);
    }

    [Fact]
    public void AnOriginOffScreenIsClampedFirst()
    {
        var p = ScreenEdge.RayToEdge(new Vector2(-500, 2000), new Vector2(0, -1), Min, Max);
        Assert.Equal(new Vector2(40, 40), p);
        Assert.True(ScreenEdge.Inside(ScreenEdge.RayToEdge(new Vector2(float.NaN, 3), new Vector2(0.3f, 0.2f), Min, Max), Min, Max));
    }

    [Fact]
    public void EveryDirectionLandsOnTheBorder()
    {
        for (var a = 0f; a < Heading.Tau; a += 0.05f)
        {
            var d = new Vector2(MathF.Cos(a), MathF.Sin(a));
            var p = ScreenEdge.RayToEdge(new Vector2(700, 300), d, Min, Max);
            Assert.True(ScreenEdge.Inside(p, Min, Max));
            var onBorder = MathF.Abs(p.X - Min.X) < 1e-2f || MathF.Abs(p.X - Max.X) < 1e-2f || MathF.Abs(p.Y - Min.Y) < 1e-2f || MathF.Abs(p.Y - Max.Y) < 1e-2f;
            Assert.True(onBorder, $"angle {a}: {p}");
            // and in the direction asked for
            Assert.True(Vector2.Dot(p - new Vector2(700, 300), d) > 0);
        }
    }

    [Fact]
    public void ScreenDirectionFromTwoProjections()
    {
        var dir = new Vector2(0, 1);
        Assert.True(ScreenEdge.Direction(new Vector2(100, 100), new Vector2(130, 60), ref dir));
        Assert.Equal(new Vector2(0.6f, -0.8f), dir);
        Assert.Equal(-MathF.Atan2(0.8f, 0.6f), ScreenEdge.Angle(dir), 1e-5f);
        // the same pixel twice: no direction, the old one is kept
        Assert.False(ScreenEdge.Direction(new Vector2(5, 5), new Vector2(5.2f, 5.1f), ref dir));
        Assert.Equal(new Vector2(0.6f, -0.8f), dir);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.8f)]
    [InlineData(1.6f)]
    [InlineData(2.9f)]
    [InlineData(-2.2f)]
    [InlineData(-0.4f)]
    public void TheFingertipPointsWhereAsked(float angle)
    {
        Span<Vector2> quad = stackalloc Vector2[4];
        foreach (var mirrored in new[] { false, true })
        {
            var model = Glove.GameSprite;
            var anchor = new Vector2(500, 400);
            Glove.Quad(model, anchor, 64f, angle, mirrored, quad);
            // the tip's spot in the picture, placed through the same quad (texture coordinates never mirror)
            var top = Vector2.Lerp(quad[0], quad[1], model.Tip.X);
            var bottom = Vector2.Lerp(quad[3], quad[2], model.Tip.X);
            var tip = Vector2.Lerp(top, bottom, model.Tip.Y);
            var expected = Glove.TipOnScreen(model, anchor, 64f, angle);
            Assert.Equal(expected.X, tip.X, 1e-2f);
            Assert.Equal(expected.Y, tip.Y, 1e-2f);
            var pointing = MathF.Atan2(tip.Y - anchor.Y, tip.X - anchor.X);
            Assert.Equal(0f, Heading.Wrap(pointing - angle), 1e-3f);
            // the pivot sits on the anchor
            var pl = Vector2.Lerp(Vector2.Lerp(quad[0], quad[1], model.Pivot.X), Vector2.Lerp(quad[3], quad[2], model.Pivot.X), model.Pivot.Y);
            Assert.Equal(anchor.X, pl.X, 1e-2f);
            Assert.Equal(anchor.Y, pl.Y, 1e-2f);
        }
    }

    [Fact]
    public void MirroringKeepsTheThumbOnTop()
    {
        // pointing left unmirrored turns the picture upside down: its top edge ends up below the pivot
        Span<Vector2> quad = stackalloc Vector2[4];
        var anchor = new Vector2(300, 300);
        Glove.Quad(Glove.GameSprite, anchor, 64f, MathF.PI, false, quad);
        var topMid = (quad[0] + quad[1]) * 0.5f;
        Assert.True(topMid.Y > anchor.Y);
        Glove.Quad(Glove.GameSprite, anchor, 64f, MathF.PI, true, quad);
        topMid = (quad[0] + quad[1]) * 0.5f;
        Assert.True(topMid.Y < anchor.Y);
    }

    [Fact]
    public void MirrorFlipsWithHysteresis()
    {
        Assert.False(Glove.Mirror(0f, false));
        Assert.True(Glove.Mirror(MathF.PI, false));
        // near straight down it keeps whatever it was
        Assert.True(Glove.Mirror(MathF.PI / 2 + 0.1f, true));
        Assert.False(Glove.Mirror(MathF.PI / 2 + 0.1f, false));
        Assert.True(Glove.Mirror(MathF.PI / 2 + 0.3f, false));
        Assert.False(Glove.Mirror(MathF.PI / 2 - 0.3f, true));
    }

    [Fact]
    public void PartUvForTheMeasuredCursor()
    {
        var (u, v, w, h) = Glove.MeasuredPart;
        // _hr1: 128×128 with the ULD's 1× numbers doubled; the whole texture
        var (min, max) = Glove.PartUv(u, v, w, h, 128, 128, 2);
        Assert.Equal(Vector2.Zero, min);
        Assert.Equal(Vector2.One, max);
        // a part in a sheet, and a nonsense one falls back to the whole texture
        (min, max) = Glove.PartUv(32, 0, 32, 16, 128, 64, 2);
        Assert.Equal(new Vector2(0.5f, 0f), min);
        Assert.Equal(new Vector2(1f, 0.5f), max);
        Assert.Equal((Vector2.Zero, Vector2.One), Glove.PartUv(10, 10, 0, 5, 64, 64, 1));
        Assert.Equal((Vector2.Zero, Vector2.One), Glove.PartUv(0, 0, 64, 64, 0, 64, 1));
    }

    [Fact]
    public void TheMeasuredSpritePointsRightAndSlightlyUp()
    {
        var a = Glove.GameSprite.AxisAngle;
        Assert.InRange(a, -0.4f, 0f);
        Assert.InRange(Glove.GameSprite.Reach, 0.25f, 0.45f);
    }

    [Fact]
    public void TheDrawnGloveIsConvexPiecesPointingRight()
    {
        var maxX = float.NegativeInfinity;
        foreach (var shape in VectorGlove.Shapes)
        {
            Assert.InRange(shape.Length, 3, 64);
            float? sign = null;
            for (var i = 0; i < shape.Length; i++)
            {
                var a = shape[i];
                var b = shape[(i + 1) % shape.Length];
                var c = shape[(i + 2) % shape.Length];
                var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
                if (MathF.Abs(cross) < 1e-7f)
                    continue;
                sign ??= MathF.Sign(cross);
                Assert.Equal(sign, MathF.Sign(cross));
                maxX = MathF.Max(maxX, a.X);
            }
        }

        // the pointing finger is the furthest right, at the model's tip
        Assert.Equal(VectorGlove.Model.Tip.X, maxX, 0.01f);
        Assert.InRange(VectorGlove.Model.AxisAngle, -0.2f, 0f);
    }

    [Fact]
    public void ColoursPackAsImGuiExpects()
    {
        Assert.Equal(0xFF0000FFu, Colors.Pack(new Vector4(1, 0, 0, 1)));
        Assert.Equal(0x80FF0000u, Colors.Pack(new Vector4(0, 0, 1, 1), 0.5f));
        Assert.Equal(0x00000000u, Colors.Pack(new Vector4(float.NaN, -1, 0, 2), 0f));
        Assert.Equal(new Vector4(1, 1, 1, 0.5f), Colors.Lighten(new Vector4(0.2f, 0.4f, 0.6f, 0.5f), 1f));
    }
}
