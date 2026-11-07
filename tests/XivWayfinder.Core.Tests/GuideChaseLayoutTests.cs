using System.Numerics;
using XivWayfinder.Core;

namespace XivWayfinder.Core.Tests;

public class GuideChaseLayoutTests
{
    private static readonly Vector3[] Corner = [new(0, 0, 0), new(0, 2, 5), new(5, 2, 5)];

    [Fact]
    public void TrailChasesFromCabooseAndTrailOffVisiblyTakesPoint()
    {
        var chase = GuideChaseLayout.Create(true, 4);
        var point = GuideChaseLayout.Create(false, 4);
        Assert.True(chase.Chasing);
        Assert.Equal(4, chase.GuideLead);
        Assert.Equal(5.75f, chase.LookAhead);
        Assert.False(point.Chasing);
        Assert.Equal(6, point.GuideLead);
        Assert.Equal(10, point.LookAhead);
        Assert.InRange(GuideChaseLayout.LightGap, .1f, 1);
    }

    [Theory]
    [InlineData(true, 4, 5.5f)]
    [InlineData(false, 4, 7.5f)]
    [InlineData(true, 12, 12)]
    [InlineData(false, 20, 12)]
    public void EagerOffsetAppliesOnceWithinMaximumLead(bool trail, float lead, float expected)
    {
        var layout = GuideChaseLayout.Create(trail, lead, true);
        Assert.Equal(expected, layout.GuideLead);
        Assert.InRange(layout.LookAhead - layout.GuideLead, 0, 4);
    }

    [Theory]
    [InlineData(float.NaN, 4)]
    [InlineData(float.PositiveInfinity, 4)]
    [InlineData(-10, 2)]
    [InlineData(100, 12)]
    public void InvalidConfiguredLeadIsBounded(float input, float expected) =>
        Assert.Equal(expected, GuideChaseLayout.Create(true, input).GuideLead);

    [Fact]
    public void DynamicLeadRetainsTheFlowLookDistanceWithoutChangingLegacyPreference()
    {
        var normal = GuideChaseLayout.Create(true, 4);
        var nearby = normal.WithLead(1);
        Assert.Equal(1, nearby.GuideLead);
        Assert.Equal(2.75f, nearby.LookAhead);
        Assert.Equal(4, normal.GuideLead);
        Assert.Equal(normal, normal.WithLead(float.NaN));
    }

    [Fact]
    public void BeforeCornerLookingStopsAtCornerRatherThanAcrossTheWall()
    {
        var pose = GuideChaseLayout.Create(true, 4).Sample(Corner, 0)!.Value;
        Assert.Equal(new Vector3(0, 1.6f, 4), pose.Position);
        Assert.Equal(Corner[1], pose.LookAt);
        Assert.Equal(4, pose.GuideArc);
        Assert.Equal(5, pose.LookAheadArc);
        Assert.Equal(Vector2.UnitY, pose.Direction);
        Assert.Equal(Vector2.UnitY, pose.LightDirection);
        Assert.Equal(Corner[1], GuideChaseLayout.CornerLimitedLookAhead(Corner, 4, 20));
    }

    [Fact]
    public void AtCornerLookingTurnsOntoOutgoingSegment()
    {
        var pose = GuideChaseLayout.Create(true, 4).SampleAtArc(Corner, 5)!.Value;
        Assert.Equal(Corner[1], pose.Position);
        Assert.Equal(new Vector3(1.75f, 2, 5), pose.LookAt);
        Assert.Equal(Vector2.UnitX, pose.Direction);
        Assert.Equal(new Vector3(2, 2, 5), GuideChaseLayout.CornerLimitedLookAhead(Corner, 5, 2));
    }

    [Fact]
    public void EveryPoseAroundCornerLiesOnPolylineRatherThanItsDiagonalChord()
    {
        var layout = GuideChaseLayout.Create(true, 4);
        for (var i = 0; i <= 200; ++i)
        {
            var arc = i / 20f;
            var pose = layout.SampleAtArc(Corner, arc)!.Value;
            Assert.InRange(Vector3.Distance(PathTrail.At(Corner, arc), pose.Position), 0, .000001f);
            Assert.True(pose.Position.X == 0 || pose.Position.Z == 5);
            Assert.InRange(pose.LookAheadArc, pose.GuideArc, 10);
            Assert.True(pose.LookAt.X == 0 || pose.LookAt.Z == 5);
        }
    }

    [Fact]
    public void NearDestinationGuideAndLightGoalClampWithoutJumpingInFront()
    {
        var layout = GuideChaseLayout.Create(true, 4);
        foreach (var progress in new[] { 6f, 8f, 9.9f, 10f, 100f })
        {
            var pose = layout.Sample(Corner, progress)!.Value;
            Assert.Equal(Corner[^1], pose.Position);
            Assert.Equal(Corner[^1], pose.LookAt);
            Assert.Equal(10, pose.GuideArc);
            Assert.Equal(10, pose.LookAheadArc);
            Assert.Equal(Vector2.UnitX, pose.Direction);
            Assert.Equal(Vector2.UnitX, pose.LightDirection);
        }
    }

    [Fact]
    public void ActualIndependentArcCanRemainBehindPlayerWithoutBecomingAnOffset()
    {
        var layout = GuideChaseLayout.Create(true, 4);
        var independent = layout.SampleAtArc(Corner, 2)!.Value;
        var attached = layout.Sample(Corner, 7)!.Value;
        Assert.Equal(2, independent.GuideArc);
        Assert.Equal(new Vector3(0, .8f, 2), independent.Position);
        Assert.NotEqual(attached.Position, independent.Position);
    }

    [Fact]
    public void DuplicateCornersDoNotCreateNanDirections()
    {
        Vector3[] path = [Corner[0], Corner[0], Corner[1], Corner[1], Corner[2], Corner[2]];
        var layout = GuideChaseLayout.Create(true, 4);
        Assert.Equal(Vector2.UnitY, layout.SampleAtArc(path, 4)!.Value.Direction);
        Assert.Equal(Vector2.UnitX, layout.SampleAtArc(path, 5)!.Value.Direction);
        Assert.Equal(Corner[^1], layout.SampleAtArc(path, 10)!.Value.Position);
    }

    [Fact]
    public void TinyButNonzeroSegmentsStillContributeTheirRouteArc()
    {
        Vector3[] path = [new(0, 0, 0), new(0, 0, .00005f), new(0, 0, 1)];
        var pose = GuideChaseLayout.Create(true, 4).SampleAtArc(path, .5f)!.Value;
        Assert.InRange(Math.Abs(pose.Position.Z - .5f), 0, .0000001f);
    }

    [Fact]
    public void MissingDegenerateAndInvalidRoutesFailClosedWithoutStraightFallback()
    {
        var layout = GuideChaseLayout.Create(true, 4);
        Vector3[][] paths = [[], [Vector3.Zero], [Vector3.Zero, Vector3.Zero],
            [Vector3.Zero, Vector3.UnitY], [Vector3.Zero, new(float.NaN)],
            [Vector3.Zero, new(float.MaxValue)], new Vector3[4097]];
        foreach (var path in paths)
        {
            Assert.Null(layout.Sample(path, 0));
            Assert.Null(layout.SampleAtArc(path, 0));
            Assert.Null(GuideChaseLayout.CornerLimitedLookAhead(path, 0, 4));
        }
        Assert.Null(layout.Sample(Corner, float.NaN));
        Assert.Null(layout.Sample(Corner, -1));
        Assert.Null(layout.SampleAtArc(Corner, float.PositiveInfinity));
        Assert.Null(GuideChaseLayout.CornerLimitedLookAhead(Corner, 1, float.NaN));
        Assert.Null(GuideChaseLayout.CornerLimitedLookAhead(Corner, -1, 1));
    }

    [Fact]
    public void CuriosityIsOccasionalSmoothAndDisabledByReducedMotion()
    {
        var active = 0;
        for (var i = 0; i < 800; ++i)
        {
            var attention = GuideChaseLayout.Attention(i / 100d, false);
            Assert.InRange(attention, 0, 1);
            if (attention > .01f) active++;
            Assert.Equal(0, GuideChaseLayout.Attention(i / 100d, true));
        }
        Assert.InRange(active, 100, 210);
        Assert.Equal(0, GuideChaseLayout.Attention(6, false));
        Assert.Equal(1, GuideChaseLayout.Attention(7, false));
        Assert.Equal(0, GuideChaseLayout.Attention(8, false));
        Assert.Equal(0, GuideChaseLayout.Attention(double.NaN, false));
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(0, -90)]
    [InlineData(179, -179)]
    [InlineData(-179, 179)]
    public void GlanceTurnsOnlyTowardTheLightAndByAtMostTwelveDegrees(float baseDegrees, float lightDegrees)
    {
        var basis = baseDegrees * MathF.PI / 180;
        var light = lightDegrees * MathF.PI / 180;
        var glanced = GuideChaseLayout.AttentiveYaw(basis, light, 1);
        var turn = Heading.Wrap(glanced - basis);
        var toward = Heading.Wrap(light - basis);
        Assert.Equal(MathF.Sign(toward), MathF.Sign(turn));
        Assert.InRange(Math.Abs(turn), 0, 12 * MathF.PI / 180 + .000001f);
        Assert.Equal(Heading.Wrap(basis), GuideChaseLayout.AttentiveYaw(basis, light, 0));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void RuntimeWholeModelTurnsAreGradualAndUseTheShortArc(int fps)
    {
        var yaw = 0f;
        var dt = 1f / fps;
        for (var i = 0; i < fps * 3; ++i)
        {
            var next = GuideChaseLayout.EaseYaw(yaw, MathF.PI, dt);
            Assert.InRange(Math.Abs(Heading.Wrap(next - yaw)), 0, MathF.PI * dt + .000001f);
            yaw = next;
        }
        Assert.InRange(Math.Abs(Heading.Wrap(MathF.PI - yaw)), 0, .001f);
        var nearWrap = 179 * MathF.PI / 180;
        var result = GuideChaseLayout.EaseYaw(nearWrap, -179 * MathF.PI / 180, dt);
        Assert.InRange(Heading.Wrap(result - nearWrap), 0, 2 * MathF.PI / 180);
        Assert.Equal(nearWrap, GuideChaseLayout.EaseYaw(nearWrap, 0, 0));
        Assert.Equal(0, GuideChaseLayout.EaseYaw(float.NaN, 0, dt));
    }
}
