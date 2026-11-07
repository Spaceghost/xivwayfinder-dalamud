using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionIntentTests
{
    private sealed class Walk
    {
        public readonly CompanionIntent Intent = new();
        public float X = 20, Arc = 20;
        public double Time;
        public Walk() => Intent.Update(new(X, 0, 0), Arc, Vector2.UnitX, Time, 4, true);
        public CompanionIntentFrame Step(float speed, float dt = .05f, float lead = 4)
        {
            Time += dt; X += speed * dt; Arc += speed * dt;
            return Intent.Update(new(X, 0, 0), Arc, Vector2.UnitX, Time, lead, true);
        }
        public CompanionIntentFrame For(float speed, float seconds)
        {
            CompanionIntentFrame result = default;
            for (var i = 0; i < (int)(seconds / .05f); i++) result = Step(speed);
            return result;
        }
    }

    [Fact]
    public void SustainedActualGoalwardMovementResumesLeading()
    {
        var walk = new Walk();
        Assert.Equal(CompanionIntentMode.Waiting, walk.Intent.Current.Mode);
        Assert.Equal(1, walk.Intent.Current.EffectiveLead);
        var result = walk.For(2, 2);
        Assert.Equal(CompanionIntentMode.Lead, result.Mode);
        Assert.InRange(result.EffectiveLead, 3.8f, 4);
        Assert.True(result.FacingTravel); Assert.True(result.GoalGlanceWeight > .95f);
    }

    [Fact]
    public void RetreatReturnsNearPlayerAndResumingGoalwardMovementLeadsAgain()
    {
        var walk = new Walk(); walk.For(2, 2);
        Assert.Equal(CompanionIntentMode.Lead, walk.For(-2, .2f).Mode);
        var retreat = walk.For(-2, 2);
        Assert.Equal(CompanionIntentMode.Returning, retreat.Mode);
        Assert.InRange(retreat.EffectiveLead, 1, 1.1f);
        Assert.True(retreat.FacingTravel); Assert.True(retreat.GoalGlanceWeight < .35f);
        Assert.Equal(CompanionIntentMode.Lead, walk.For(2, .7f).Mode);
    }

    [Fact]
    public void PausingReturnsThenWaitsNearThePlayer()
    {
        var walk = new Walk(); walk.For(2, 2);
        Assert.Equal(CompanionIntentMode.Lead, walk.For(0, .4f).Mode);
        var waiting = walk.For(0, 3);
        Assert.Equal(CompanionIntentMode.Waiting, waiting.Mode);
        Assert.InRange(waiting.EffectiveLead, 1, 1.08f); Assert.False(waiting.FacingTravel);
    }

    [Fact]
    public void SidewaysDepartureAlsoReturnsWithoutDependingOnPlayerFacing()
    {
        var walk = new Walk(); walk.For(2, 2);
        CompanionIntentFrame result = default;
        for (var i = 1; i <= 50; i++)
            result = walk.Intent.Update(new(walk.X, 0, i * .1f), walk.Arc, Vector2.UnitX, walk.Time + i * .05, 4, true);
        Assert.Equal(CompanionIntentMode.Returning, result.Mode);
        Assert.InRange(result.EffectiveLead, 1, 1.1f);
    }

    [Fact]
    public void HeadingChangesWithoutDisplacementCannotStartLeadingOrSelectAnything()
    {
        var intent = new CompanionIntent();
        for (var i = 0; i < 120; i++)
        {
            var tangent = i % 2 == 0 ? Vector2.UnitX : -Vector2.UnitX;
            var frame = intent.Update(Vector3.Zero, 0, tangent, i * .05, 4, true);
            Assert.Equal(CompanionIntentMode.Waiting, frame.Mode);
            Assert.Equal(1, frame.EffectiveLead); Assert.False(frame.FacingTravel);
        }
    }

    [Fact]
    public void RouteProgressRecognizesTurningAcrossTheNewTangent()
    {
        var walk = new Walk(); walk.For(2, 2);
        for (var i = 1; i <= 15; i++)
            Assert.Equal(CompanionIntentMode.Lead, walk.Intent.Update(new(walk.X + i * .1f, 0, 0),
                walk.Arc + i * .1f, Vector2.UnitY, walk.Time + i * .05, 4, true).Mode);
    }

    [Fact]
    public void ReversalClearsDirectionalEvidenceRatherThanAccumulatingIt()
    {
        var walk = new Walk();
        for (var i = 0; i < 10; i++) { walk.For(2, .15f); walk.For(-2, .15f); }
        Assert.NotEqual(CompanionIntentMode.Lead, walk.Intent.Current.Mode);
        Assert.Equal(CompanionIntentMode.Lead, walk.For(2, .7f).Mode);
    }

    [Fact]
    public void EffectiveOffsetAndAttentionAreBoundedWithoutHardFlips()
    {
        var walk = new Walk(); var previous = walk.Intent.Current;
        for (var i = 0; i < 240; i++)
        {
            var next = walk.Step(i < 70 || i >= 170 ? 3 : -3);
            Assert.InRange(Math.Abs(next.EffectiveLead - previous.EffectiveLead), 0, .20001f);
            Assert.InRange(next.EffectiveLead, 1, 4);
            Assert.InRange(Math.Abs(next.GoalGlanceWeight - previous.GoalGlanceWeight), 0, .301f);
            previous = next;
        }
    }

    [Fact]
    public void WaitingOccasionallyGlancesGoalwardWithoutChangingItsOffset()
    {
        var walk = new Walk(); var maximum = 0f;
        for (var i = 0; i < 180; i++)
        {
            var frame = walk.Step(0); maximum = Math.Max(maximum, frame.GoalGlanceWeight);
            Assert.Equal(1, frame.EffectiveLead); Assert.Equal(CompanionIntentMode.Waiting, frame.Mode);
        }
        Assert.InRange(maximum, .5f, .65f);
        walk.For(0, .6f); // allow the deliberately eased glance tail to settle
        Assert.True(walk.Intent.Current.GoalGlanceWeight < .01f);
    }

    [Fact]
    public void LoadingTimeReversalAndRouteRebaseAreNotGoalwardEvidence()
    {
        var walk = new Walk(); walk.For(2, 2);
        var before = walk.Intent.Current.EffectiveLead;
        var gap = walk.Intent.Update(new(walk.X,0,0), walk.Arc, Vector2.UnitX, walk.Time + 5, 4, true);
        Assert.NotEqual(CompanionIntentMode.Lead, gap.Mode);
        Assert.InRange(before - gap.EffectiveLead, 0, .40001f);
        var reversal = walk.Intent.Update(new(walk.X,0,0), walk.Arc, Vector2.UnitX, walk.Time, 4, true);
        Assert.Equal(gap.EffectiveLead, reversal.EffectiveLead);
        var rebase = walk.Intent.Update(new(walk.X,0,0), walk.Arc + 100, Vector2.UnitX, walk.Time + .05, 4, true);
        Assert.NotEqual(CompanionIntentMode.Lead, rebase.Mode);
    }

    [Fact]
    public void InvalidOrMissingRouteResetsAndCannotPoisonFutureSamples()
    {
        var walk = new Walk(); walk.For(2, 1);
        var noRoute = walk.Intent.Update(Vector3.Zero, 0, Vector2.UnitX, 2, 4, false);
        Assert.Equal(new(CompanionIntentMode.Waiting, 1, 0, false), noRoute);
        Assert.Equal(noRoute, walk.Intent.Update(new(float.NaN), 0, Vector2.UnitX, 2, 4, true));
        Assert.Equal(noRoute, walk.Intent.Update(Vector3.Zero, float.NaN, Vector2.UnitX, 2, 4, true));
        Assert.Equal(noRoute, walk.Intent.Update(Vector3.Zero, 0, Vector2.Zero, 2, 4, true));
        Assert.Equal(noRoute, walk.Intent.Update(Vector3.Zero, 0, Vector2.UnitX, double.NaN, 4, true));
        walk.Intent.Reset();
        Assert.Equal(noRoute, walk.Intent.Current);
    }
}
