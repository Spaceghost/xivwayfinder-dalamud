using System.Numerics;

namespace XivWayfinder.Core.Tests;

/// <summary>
/// The maths the overlay runs every frame must not make garbage: the game stutters on collections. Bytes per
/// frame on one thread, from the runtime's own counter.
/// </summary>
public sealed class AllocationBudgetTests
{
    private static long PerOperation(Action action, int n = 5000)
    {
        for (var i = 0; i < 500; i++)
            action();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < n; i++)
            action();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / n;
    }

    private static readonly Vector3[] Path = [new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new(20, 5, 10), new(30, 5, 25)];

    [Fact]
    public void AFrameOfPointerMathsAllocatesNothing()
    {
        var t = 0.0;
        var mirrored = false;
        var sink = 0f;
        var bytes = PerOperation(() =>
        {
            t += 1.0 / 60;
            var player = new Vector3(3, 0, 1);
            var dir = Heading.DirectionTo(player, Path[PathFollow.NextCorner(Path, player, 1.5f)]);
            var turn = Heading.TurnAway(0.4f, dir);
            var glow = Pulse.Approach(0.3f, Pulse.Glow(turn, Pulse.Breath(t, 2.6f), 0.28f, 1f), 1f / 60, 0.15f);
            var screenDir = new Vector2(0, 1);
            ScreenEdge.Direction(new Vector2(960, 600), new Vector2(990, 560), ref screenDir);
            var edge = ScreenEdge.RayToEdge(new Vector2(960, 600), screenDir, new Vector2(40, 40), new Vector2(1880, 1040));
            var angle = ScreenEdge.Angle(screenDir);
            mirrored = Glove.Mirror(angle, mirrored);
            Span<Vector2> quad = stackalloc Vector2[4];
            Glove.Quad(Glove.GameSprite, edge, 44f, angle, mirrored, quad);
            foreach (var shape in VectorGlove.Shapes)
                sink += Glove.Place(shape[0], VectorGlove.Model.AxisAngle, edge, 22f, angle, mirrored).X;
            for (var i = 0; i < 10; i++)
                sink += Pulse.Trail(i, 10, t, 1.8f);
            sink += glow + quad[2].X + Pulse.Tap(t, 1.1f) + Pulse.Bob(t, 2.2f, 0.12f) + Colors.Pack(Colors.Gold, glow);
            sink += PathFollow.DistanceFromPath(Path, player);
        });
        Assert.True(bytes == 0, $"{bytes} bytes allocated per frame, budget 0");
        Assert.True(float.IsFinite(sink));
    }
}
