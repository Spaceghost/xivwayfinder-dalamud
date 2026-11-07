using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class GuideConnectorTests
{
    private static readonly CompanionIntentFrame Lead = new(CompanionIntentMode.Lead, 4, 1, true);
    private static Vector3[] MainRoute() => [new(10, 0, 0), new(10, 0, 20), new(30, 2, 20)];
    private static CompanionRouteMotionFrame Held(Vector3 position) =>
        new([position, position + Vector3.UnitZ], 0, 0, position, true, true);
    private static TaskCompletionSource<IReadOnlyList<Vector3>> Pending() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public void PlanKeepsQueryCornersAndUnchangedMainSuffix()
    {
        var main = MainRoute();
        Vector3[] queried = [Vector3.Zero, new(0, 0, 5), new(10, 0, 5)];
        Assert.True(GuideConnectorPlan.TryCreate(main, Vector3.Zero, 5, queried, out var plan));
        Assert.NotNull(plan);
        Assert.Equal(15, plan.ConnectorLength);
        Assert.Equal(new[] { Vector3.Zero, new(0, 0, 5), new(10, 0, 5), new(10, 0, 20), new(30, 2, 20) }, plan.Route);
        Assert.Same(main, plan.TargetRoute);
        Assert.True(plan.MatchesTarget(main));
        queried[1] = new(999, 0, 999);
        Assert.Equal(new Vector3(0, 0, 5), plan.Route[1]);
        main[1] += Vector3.UnitY;
        Assert.False(plan.MatchesTarget(main));
    }

    [Fact]
    public void MissingOrPartialQueryEndpointsNeverBecomeSyntheticConnections()
    {
        var main = MainRoute();
        Assert.False(GuideConnectorPlan.TryCreate(main, Vector3.Zero, 5, [new(1, 0, 0), new(10, 0, 5)], out _));
        Assert.False(GuideConnectorPlan.TryCreate(main, Vector3.Zero, 5, [Vector3.Zero, new(9, 0, 5)], out _));
        Assert.False(GuideConnectorPlan.TryCreate(main, Vector3.Zero, 5, [Vector3.Zero], out _));
    }

    [Fact]
    public void OnlyNumericEndpointRoundoffIsNormalized()
    {
        Assert.True(GuideConnectorPlan.TryCreate(MainRoute(), Vector3.Zero, 5,
            [new(.001f, 0, 0), new(10.001f, 0, 5)], out var plan));
        Assert.Equal(Vector3.Zero, plan!.Route[0]);
        Assert.Equal(new Vector3(10, 0, 5), plan.Route[1]);
    }

    [Fact]
    public void InvalidCoordinatesLengthsAndVerticalOnlyLegsFailClosed()
    {
        var main = MainRoute();
        Assert.False(GuideConnectorPlan.TryCreate(main, Vector3.Zero, float.NaN, [Vector3.Zero, main[0]], out _));
        Assert.False(GuideConnectorPlan.TryCreate(main, Vector3.Zero, 0,
            [Vector3.Zero, new(float.NaN, 0, 0), main[0]], out _));
        Assert.False(GuideConnectorPlan.TryCreate(main, Vector3.Zero, 0,
            [Vector3.Zero, new(0, 2, 0), main[0]], out _));
        Assert.False(GuideConnectorPlan.TryCreate(main, Vector3.Zero, 0,
            [Vector3.Zero, new(2000, 0, 0), main[0]], out _));
        var many = Enumerable.Repeat(Vector3.Zero, 513).ToArray(); many[^1] = main[0];
        Assert.False(GuideConnectorPlan.TryCreate(main, Vector3.Zero, 0, many, out _));
        Assert.False(GuideConnectorPlan.TryCreate(main, Vector3.Zero, -1, [Vector3.Zero, main[0]], out _));
        Assert.False(GuideConnectorPlan.TryCreate(main, Vector3.Zero, 10000, [Vector3.Zero, main[0]], out _));
    }

    [Fact]
    public void JoinUsesTheCorrectHeightLayer()
    {
        Vector3[] layered = [new(0, 0, 0), new(10, 0, 0), new(10, 10, 10), new(0, 10, 0)];
        Assert.True(GuideConnectorPlan.TryJoin(layered, new(0, 10, 0), out var arc, out var join));
        Assert.Equal(PathTrail.Length(layered), arc);
        Assert.Equal(new Vector3(0, 10, 0), join);
    }

    [Fact]
    public void AdoptionAndReturnPrescribeExactArcOnlyOnTransitions()
    {
        var main = MainRoute(); var job = Pending();
        using var connector = new GuideConnectorCoordinator((_, _, _) => job.Task);
        var held = Held(Vector3.Zero);
        var initial = connector.Select(1, main, 0, Lead, held, 0, true);
        Assert.Same(main, initial.Route); Assert.Null(initial.RebaseArc);
        job.SetResult([Vector3.Zero, new(0, 0, -5), new(10, 0, -5), main[0]]);
        var adopt = connector.Select(1, main, 0, Lead, held, .1, true);
        Assert.True(adopt.Connecting); Assert.Equal(0f, adopt.RebaseArc);
        Assert.Equal(CompanionIntentMode.Returning, adopt.Intent.Mode);
        var following = new CompanionRouteMotionFrame(adopt.Route, 2, 1, PathTrail.At(adopt.Route, 2), true, false);
        var stay = connector.Select(1, main, 0, Lead, following, .2, true);
        Assert.Same(adopt.Route, stay.Route); Assert.Null(stay.RebaseArc);
        var suffixArc = adopt.ProgressArc + 3;
        following = following with { Arc = suffixArc, Position = PathTrail.At(adopt.Route, suffixArc) };
        var rejoin = connector.Select(1, main, 0, Lead, following, .3, true);
        Assert.False(rejoin.Connecting); Assert.Same(main, rejoin.Route);
        Assert.Equal(3f, rejoin.RebaseArc);
        Assert.Equal(Lead, rejoin.Intent);
        var retry = connector.Select(1, main, 0, Lead, following, .4, true);
        Assert.Equal(rejoin.RebaseArc, retry.RebaseArc); // retain precise arc if the caller could not adopt
        var accepted = following with { Route = main, Arc = 3, Position = PathTrail.At(main, 3) };
        Assert.Null(connector.Select(1, main, 0, Lead, accepted, .5, true).RebaseArc);
    }

    [Fact]
    public void IntegratedMotionWalksEveryConnectorCornerBeforeRejoining()
    {
        var main = MainRoute(); var job = Pending();
        using var connector = new GuideConnectorCoordinator((_, _, _) => job.Task);
        var motion = new CompanionRouteMotion();
        Vector3[] old = [Vector3.Zero, new(0, 0, 1)];
        motion.Update(old, 0, Lead, 0, true);
        motion.Update(main, 0, Lead, 0, true);
        connector.Select(1, main, 0, Lead, motion.Current, 0, true);
        job.SetResult([Vector3.Zero, new(0, 0, -5), new(10, 0, -5), main[0]]);
        var seenRearCorner = false; var rejoined = false; var previous = motion.Current.Position;
        for (var i = 1; i <= 400; i++)
        {
            var selection = connector.Select(1, main, 0, Lead, motion.Current, i * .05, true);
            if (selection.RebaseArc is { } at) Assert.True(motion.TryRebase(selection.Route, at));
            var frame = motion.Update(selection.Route, selection.ProgressArc, selection.Intent, i * .05, true);
            Assert.InRange(Vector3.Distance(previous, frame.Position), 0, CompanionRouteMotion.MaximumSpeed * .05f + .002f);
            if (selection.Connecting)
            {
                Assert.True(frame.Position.X == 0 || Math.Abs(frame.Position.Z + 5) < .0001f || frame.Position.X == 10);
                seenRearCorner |= frame.Position.X > 2 && Math.Abs(frame.Position.Z + 5) < .0001f;
            }
            else if (seenRearCorner && ReferenceEquals(frame.Route, main)) { rejoined = true; break; }
            previous = frame.Position;
        }
        Assert.True(seenRearCorner); Assert.True(rejoined);
    }

    [Fact]
    public void CancelledUncooperativeQueryKeepsSingleSlotAndCannotPublishAcrossZones()
    {
        var job = Pending(); var calls = 0; CancellationToken token = default;
        using var connector = new GuideConnectorCoordinator((_, _, cancel) => { calls++; token = cancel; return job.Task; });
        var main = MainRoute(); var held = Held(Vector3.Zero);
        connector.Select(1, main, 0, Lead, held, 0, true);
        connector.Select(2, main, 0, Lead, held, 3, true);
        Assert.True(token.IsCancellationRequested); Assert.Equal(1, calls); Assert.True(connector.QueryInFlight);
        job.SetResult([Vector3.Zero, main[0]]);
        var stale = connector.Select(2, main, 0, Lead, held, 3.1, true);
        Assert.False(stale.Connecting); Assert.Same(main, stale.Route);
        Assert.Equal(2, calls); // slot reused only after old completion is observed
    }

    [Fact]
    public void NewGoalIdentityCancelsOldQueryEvenWithIdenticalCoordinates()
    {
        var job = Pending(); CancellationToken token = default;
        using var connector = new GuideConnectorCoordinator((_, _, cancel) => { token = cancel; return job.Task; });
        var main = MainRoute(); var held = Held(Vector3.Zero);
        connector.Select(1, main, 0, Lead, held, 0, true);
        connector.Select(1, (Vector3[])main.Clone(), 0, Lead, held, .1, true);
        Assert.True(token.IsCancellationRequested);
        job.SetResult([Vector3.Zero, main[0]]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedHeldStartOrSourceSnapshotRejectsCompletedQuery(bool moveStart)
    {
        var job = Pending(); var main = MainRoute(); var held = Held(Vector3.Zero);
        using var connector = new GuideConnectorCoordinator((_, _, _) => job.Task);
        connector.Select(1, main, 0, Lead, held, 0, true);
        job.SetResult([Vector3.Zero, main[0]]);
        if (moveStart) held = held with { Position = new(.1f, 0, 0) };
        else main[1] += Vector3.UnitY;
        var result = connector.Select(1, main, 0, Lead, held, .1, true);
        Assert.False(result.Connecting); Assert.Null(result.RebaseArc);
    }

    [Fact]
    public void ActiveSourceMutationInvalidatesConnector()
    {
        var main = MainRoute();
        using var connector = new GuideConnectorCoordinator((_, _, _) => Task.FromResult<IReadOnlyList<Vector3>>([Vector3.Zero, main[0]]));
        var held = Held(Vector3.Zero);
        connector.Select(1, main, 0, Lead, held, 0, true);
        var active = connector.Select(1, main, 0, Lead, held, .1, true);
        Assert.True(active.Connecting);
        main[1] += Vector3.UnitY;
        Assert.False(connector.Select(1, main, 0, Lead, held, .2, true).Connecting);
    }

    [Fact]
    public void TimeoutCancelsWithoutFanOutAndRetriesRespectCooldown()
    {
        var job = Pending(); var calls = 0; CancellationToken token = default;
        using var connector = new GuideConnectorCoordinator((_, _, cancel) => { calls++; token = cancel; return job.Task; });
        var main = MainRoute(); var held = Held(Vector3.Zero);
        connector.Select(1, main, 0, Lead, held, 0, true);
        connector.Select(1, main, 0, Lead, held, 15, true);
        connector.Select(1, main, 0, Lead, held, 20, true);
        Assert.True(token.IsCancellationRequested); Assert.Equal(1, calls);
        job.SetCanceled();
        connector.Select(1, main, 0, Lead, held, 21, true);
        Assert.Equal(2, calls);
        connector.Select(1, main, 0, Lead, held, 21.1, true);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void FailingProviderIsBoundedAndCannotBreakUpdate()
    {
        var calls = 0;
        using var connector = new GuideConnectorCoordinator((_, _, _) => { calls++; throw new InvalidOperationException(); });
        var main = MainRoute(); var held = Held(Vector3.Zero);
        for (var i = 0; i < 40; i++) Assert.False(connector.Select(1, main, 0, Lead, held, i * .1, true).Connecting);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ResetTimeReversalAndDisposeCancelAndDoNotStartNewWork()
    {
        var job = Pending(); var calls = 0; CancellationToken token = default;
        var connector = new GuideConnectorCoordinator((_, _, cancel) => { calls++; token = cancel; return job.Task; });
        var main = MainRoute(); var held = Held(Vector3.Zero);
        connector.Select(1, main, 0, Lead, held, 10, true);
        connector.Select(1, main, 0, Lead, held, 9, true);
        Assert.True(token.IsCancellationRequested);
        connector.Reset(); connector.Dispose(); connector.Dispose();
        job.SetException(new InvalidOperationException("Observed after disposal"));
        connector.Select(1, main, 0, Lead, held, 100, true);
        Assert.Equal(1, calls); Assert.False(connector.QueryInFlight);
    }
}
