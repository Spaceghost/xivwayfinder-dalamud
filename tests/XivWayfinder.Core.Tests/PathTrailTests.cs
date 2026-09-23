using System.Numerics;

namespace XivWayfinder.Core.Tests;

/// <summary>Path highlighting: the route measured along its length, the player placed on it, the stretch ahead sampled.</summary>
public sealed class PathTrailTests
{
    // an L, then up a ramp: 10 east, 10 south, then 10 east rising 5
    private static readonly Vector3[] Path =
    [
        new(0, 0, 0),
        new(10, 0, 0),
        new(10, 0, 10),
        new(20, 5, 10),
    ];

    [Fact]
    public void LengthIsOnTheGround()
    {
        Assert.Equal(30f, PathTrail.Length(Path), 1e-4f);
        Assert.Equal(0f, PathTrail.Length([]));
        Assert.Equal(0f, PathTrail.Length([new Vector3(1, 2, 3)]));
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]
    [InlineData(5f, 5f, 0f, 0f)]
    [InlineData(10f, 10f, 0f, 0f)]
    [InlineData(15f, 10f, 5f, 0f)]
    [InlineData(25f, 15f, 10f, 2.5f)]
    [InlineData(30f, 20f, 10f, 5f)]
    [InlineData(99f, 20f, 10f, 5f)]
    [InlineData(-4f, 0f, 0f, 0f)]
    public void PointsAlongTheRoute(float arc, float x, float z, float y)
    {
        var at = PathTrail.At(Path, arc);
        Assert.Equal(x, at.X, 1e-4f);
        Assert.Equal(y, at.Y, 1e-4f);
        Assert.Equal(z, at.Z, 1e-4f);
    }

    [Fact]
    public void ThePlayerIsPlacedOnTheNearestStretch()
    {
        Assert.Equal(3f, PathTrail.Project(Path, new Vector3(3, 0, 1), out var away), 1e-4f);
        Assert.Equal(1f, away, 1e-4f);
        Assert.Equal(14f, PathTrail.Project(Path, new Vector3(11, 0, 4), out away), 1e-4f);
        Assert.Equal(1f, away, 1e-4f);
        // on the ramp, at the right height
        Assert.Equal(25f, PathTrail.Project(Path, new Vector3(15, 2.5f, 10), out away), 1e-4f);
        Assert.Equal(0f, away, 1e-4f);
        // at the shared corner the later stretch wins
        Assert.Equal(10f, PathTrail.Project(Path, new Vector3(10, 0, 0), out _), 1e-4f);
    }

    [Fact]
    public void ARampOverheadIsNotTheOneUnderfoot()
    {
        // a switchback: east along the ground, then back west 8 yalms up, right above it
        Vector3[] switchback = [new(0, 0, 0), new(20, 0, 0), new(20, 8, 1), new(0, 8, 1)];
        var ground = PathTrail.Project(switchback, new Vector3(6, 0, 0.8f), out _);
        Assert.Equal(6f, ground, 1e-3f);
        var upstairs = PathTrail.Project(switchback, new Vector3(6, 8, 0.8f), out _);
        Assert.True(upstairs > 30f, $"upstairs {upstairs}");
    }

    [Fact]
    public void EmptyAndSinglePointRoutes()
    {
        Assert.Equal(0f, PathTrail.Project([], new Vector3(1, 0, 1), out var away));
        Assert.Equal(float.PositiveInfinity, away);
        Assert.Equal(0f, PathTrail.Project([new Vector3(3, 0, 4)], Vector3.Zero, out away));
        Assert.Equal(5f, away, 1e-4f);
        Assert.Equal(Vector3.Zero, PathTrail.At([], 3f));
        Span<TrailPoint> into = stackalloc TrailPoint[8];
        Assert.Equal(0, PathTrail.Sample([], 0f, 1f, 0f, 10f, into));
        Assert.Equal(0, PathTrail.Sample([new Vector3(1, 1, 1)], 0f, 1f, 0f, 10f, into));
    }

    [Fact]
    public void SamplesTheStretchAheadAtFixedPlacesOnTheRoute()
    {
        Span<TrailPoint> into = stackalloc TrailPoint[32];
        // standing 3.3 yalms along: beads every 2.5 from the route's start, more than 0.8 and at most 12 ahead
        var n = PathTrail.Sample(Path, 3.3f, 2.5f, 0.8f, 12f, into);
        float[] arcs = [5f, 7.5f, 10f, 12.5f, 15f];
        Assert.Equal(arcs.Length, n);
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(arcs[i] - 3.3f, into[i].Ahead, 1e-4f);
            var want = PathTrail.At(Path, arcs[i]);
            Assert.Equal(want.X, into[i].Position.X, 1e-4f);
            Assert.Equal(want.Y, into[i].Position.Y, 1e-4f);
            Assert.Equal(want.Z, into[i].Position.Z, 1e-4f);
        }

        // around the bend: the bead at 12.5 is on the second stretch, not on a straight line from the player
        Assert.Equal(new Vector3(10, 0, 2.5f), into[3].Position);
    }

    [Fact]
    public void BeadsStayPutInTheWorldAndDropAwayBehind()
    {
        Span<TrailPoint> a = stackalloc TrailPoint[32];
        Span<TrailPoint> b = stackalloc TrailPoint[32];
        var na = PathTrail.Sample(Path, 1f, 2.5f, 0.8f, 20f, a);
        var nb = PathTrail.Sample(Path, 4.5f, 2.5f, 0.8f, 20f, b);
        // after walking 3.5 yalms the beads at 2.5 and 5 are behind (5 is within 0.8), the rest are where they were
        Assert.Equal(a[0].Position, new Vector3(2.5f, 0, 0));
        Assert.Equal(new Vector3(7.5f, 0, 0), b[0].Position);
        for (var i = 0; i < nb; i++)
        {
            var same = false;
            for (var j = 0; j < na; j++)
                same |= a[j].Position == b[i].Position;
            Assert.True(same || b[i].Ahead > 20f - 3.5f - 1e-3f, $"bead {i} at {b[i].Position} moved");
        }
    }

    [Fact]
    public void NeverPastTheRoutesEndOrTheBuffer()
    {
        Span<TrailPoint> into = stackalloc TrailPoint[64];
        var n = PathTrail.Sample(Path, 26f, 1f, 0f, 50f, into);
        Assert.Equal(4, n); // 27, 28, 29, 30
        Assert.Equal(Path[^1], into[n - 1].Position);
        Span<TrailPoint> small = stackalloc TrailPoint[3];
        Assert.Equal(3, PathTrail.Sample(Path, 0f, 1f, 0f, 30f, small));
        // nonsense in, nothing out
        Assert.Equal(0, PathTrail.Sample(Path, float.NaN, 1f, 0f, 30f, into));
        Assert.Equal(0, PathTrail.Sample(Path, 0f, 0f, 0f, 30f, into));
        Assert.Equal(0, PathTrail.Sample(Path, 0f, 1f, 5f, 5f, into));
    }

    [Fact]
    public void ZeroLengthStretchesAreHarmless()
    {
        Vector3[] doubled = [new(0, 0, 0), new(5, 0, 0), new(5, 0, 0), new(5, 0, 5)];
        Assert.Equal(10f, PathTrail.Length(doubled), 1e-4f);
        Span<TrailPoint> into = stackalloc TrailPoint[16];
        var n = PathTrail.Sample(doubled, 0f, 1f, 0f, 10f, into);
        Assert.Equal(10, n);
        Assert.True(Vector3.Distance(new Vector3(5, 0, 1), into[5].Position) < 1e-4f);
        Assert.Equal(7.5f, PathTrail.Project(doubled, new Vector3(5.5f, 0, 2.5f), out _), 1e-4f);
    }

    [Fact]
    public void LookAheadFollowsTheBend()
    {
        // standing 8 along the first stretch, 4 ahead is round the corner
        var ahead = PathTrail.LookAhead(Path, 8f, 4f);
        Assert.True(Vector3.Distance(new Vector3(10, 0, 2), ahead) < 1e-4f, $"{ahead}");
        var dir = Heading.DirectionTo(new Vector3(8, 0, 0), ahead);
        Assert.True(dir.Y > 0.5f, $"points round the bend: {dir}");
        // and never past the end
        Assert.Equal(Path[^1], PathTrail.LookAhead(Path, 29f, 4f));
    }

    [Fact]
    public void TheStraightFallbackIsTodaysLine()
    {
        Span<TrailPoint> into = stackalloc TrailPoint[16];
        var n = PathTrail.Straight(new Vector3(1, 7, 1), new Vector2(0, 1), 2.5f, 10, 9f, into);
        Assert.Equal(3, n); // 2, 4.5, 7; 9.5 is past the target
        Assert.Equal(new Vector3(1, 7, 8), into[2].Position);
        Assert.Equal(7f, into[2].Ahead);
        Assert.Equal(0, PathTrail.Straight(Vector3.Zero, Vector2.Zero, 2.5f, 10, 50f, into));
    }

    [Fact]
    public void TheShimmerTravelsOutwardAlongTheRoute()
    {
        // the crest is where t / period says, and the near end is brighter than the far end on average
        Assert.True(Pulse.TrailAt(0.5f, 0.9, 1.8f) > Pulse.TrailAt(0.0f, 0.9, 1.8f));
        Assert.True(Pulse.TrailAt(0.1f, 0.18, 1.8f) > Pulse.TrailAt(0.6f, 0.18, 1.8f));
        Assert.Equal(Pulse.Trail(3, 10, 1.1, 1.8f), Pulse.TrailAt(0.35f, 1.1, 1.8f), 1e-6f);
        Assert.Equal(0f, Pulse.TrailAt(float.NaN, 1, 1.8f));
        for (var a = 0f; a <= 1f; a += 0.05f)
            Assert.InRange(Pulse.TrailAt(a, 0.7, 1.8f), 0f, 1f);
    }
}
