using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionApproachCoordinatorTests
{
    private static readonly Vector3[] OldRoute = [Vector3.Zero, new(10, 0, 0)];
    private static readonly Vector3 Goal = new(5, 0, 5);
    private static readonly CompanionIntentFrame Intent = new(CompanionIntentMode.Returning, 1, 0, true);
    private static CompanionRouteMotionFrame At(float arc = 2) => new(OldRoute, arc, 0, PathTrail.At(OldRoute, arc), true, false);
    private static TaskCompletionSource<IReadOnlyList<Vector3>> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Vector3[] Path(Vector3 start, Vector3? end = null) => [start, new(start.X, start.Y, 5), end ?? Goal];

    [Fact]
    public void WaitingHoldsOldRouteAndUsesAcceptedGuideRatherThanPlayerAsOrigin()
    {
        var work = Pending(); Vector3 origin = default, target = default;
        using var coordinator = new CompanionApproachCoordinator((from, to, _) => { origin = from; target = to; return work.Task; });
        var motion = At(); var waiting = coordinator.Select(100, Goal, new(30, 0, 30), motion, 0, true);
        Assert.Equal(motion.Position, origin); Assert.Equal(Goal, target);
        Assert.True(waiting.HoldPosition); Assert.False(waiting.Ready); Assert.Same(OldRoute, waiting.Route);
        Assert.Equal(motion.Arc, waiting.ProgressArc); Assert.Null(waiting.RebaseArc);
        work.SetResult(Path(origin));
        var ready = coordinator.Select(100, Goal, new(31, 0, 31), motion, .1, true);
        Assert.True(ready.Ready); Assert.False(ready.HoldPosition); Assert.Equal(0, ready.RebaseArc);
        Assert.Equal(origin, ready.Route[0]); Assert.Equal(Goal, ready.Route[^1]);
        Assert.Equal(PathTrail.Length(ready.Route), ready.ProgressArc);
    }

    [Fact]
    public void UnspawnedGuideQueriesFromPlayerAndExplicitlySeedsAtArcZero()
    {
        var player = new Vector3(2, 0, 0);
        using var coordinator = new CompanionApproachCoordinator((from, _, _) =>
        { Assert.Equal(player, from); return Task.FromResult<IReadOnlyList<Vector3>>(Path(from)); });
        var motion = new CompanionRouteMotion();
        var selected = coordinator.Select(100, Goal, player, motion.Current, 0, true);
        Assert.True(selected.Ready); Assert.Equal(0, selected.RebaseArc);
        motion.Update(selected.Route, selected.RebaseArc!.Value, Intent, 0, true); motion.ResolvePlacement(true);
        Assert.Equal(player, motion.Current.Position); Assert.NotEqual(Goal, motion.Current.Position);
        var owned = coordinator.Select(100, Goal, player, motion.Current, .1, true);
        Assert.True(owned.Ready); Assert.Null(owned.RebaseArc);
    }

    [Fact]
    public void ExistingLongOwnedRouteIsNotSubjectToTheSmallerNewQueryLimit()
    {
        var route = Enumerable.Range(0, 600).Select(i => new Vector3(i * .01f, 0, 0)).ToArray();
        var accepted = new CompanionRouteMotionFrame(route, 2, 0, PathTrail.At(route, 2), true, false);
        using var coordinator = new CompanionApproachCoordinator((from, _, _) => Task.FromResult<IReadOnlyList<Vector3>>(Path(from)));
        var ready = coordinator.Select(100, Goal, Vector3.Zero, accepted, 0, true);
        Assert.True(ready.Ready); Assert.Equal(accepted.Position, ready.Route[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedGuideOrUnspawnedPlayerOriginRejectsLateResultWithoutBridge(bool spawned)
    {
        var work = Pending(); CancellationToken cancellation = default;
        using var coordinator = new CompanionApproachCoordinator((_, _, token) => { cancellation = token; return work.Task; });
        var before = spawned ? At() : default;
        var player = new Vector3(2, 0, 0);
        coordinator.Select(100, Goal, player, before, 0, true);
        work.SetResult(Path(player));
        var moved = spawned ? At(2.01f) : default;
        var result = coordinator.Select(100, Goal, spawned ? player : player + Vector3.UnitX * .01f, moved, .1, true);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(result.Ready); Assert.True(result.HoldPosition);
        if (spawned) Assert.Same(moved.Route, result.Route);
        else Assert.Empty(result.Route);
    }

    [Fact]
    public void SpawnStateChangeCannotAdoptAnOldPlayerOrigin()
    {
        var work = Pending();
        using var coordinator = new CompanionApproachCoordinator((_, _, _) => work.Task);
        coordinator.Select(100, Goal, At().Position, default, 0, true);
        work.SetResult(Path(At().Position));
        Assert.False(coordinator.Select(100, Goal, At().Position, At(), .1, true).Ready);
    }

    [Fact]
    public void NearbyReturnedEndpointIsRetainedWithoutExactGoalBridge()
    {
        var end = Goal + new Vector3(.3f, .4f, .3f);
        using var coordinator = new CompanionApproachCoordinator((from, _, _) => Task.FromResult<IReadOnlyList<Vector3>>(Path(from, end)));
        var selected = coordinator.Select(100, Goal, Vector3.Zero, At(), 0, true);
        Assert.True(selected.Ready); Assert.Equal(3, selected.Route.Length);
        Assert.Equal(end, selected.Route[^1]); Assert.DoesNotContain(Goal, selected.Route);
    }

    [Theory]
    [InlineData(.501f, 0, 0)]
    [InlineData(.4f, 0, .4f)]
    [InlineData(0, .501f, 0)]
    [InlineData(0, -.501f, 0)]
    public void PartialEndpointOutsideArrivalRegionIsNotExtended(float x, float y, float z)
    {
        using var coordinator = new CompanionApproachCoordinator((from, _, _) =>
            Task.FromResult<IReadOnlyList<Vector3>>(Path(from, Goal + new Vector3(x, y, z))));
        var selected = coordinator.Select(100, Goal, Vector3.Zero, At(), 0, true);
        Assert.False(selected.Ready); Assert.True(selected.HoldPosition); Assert.Same(OldRoute, selected.Route);
    }

    [Theory]
    [InlineData(0)] // missing start
    [InlineData(1)] // truncated path
    [InlineData(2)] // nonfinite
    [InlineData(3)] // excessive corners
    [InlineData(4)] // vertical-only leg
    [InlineData(5)] // zero length
    [InlineData(6)] // enormous detour
    public void MalformedOrIncompletePathCannotBecomeReady(int kind)
    {
        using var coordinator = new CompanionApproachCoordinator((from, _, _) => Task.FromResult<IReadOnlyList<Vector3>>(kind switch
        {
            0 => [from + Vector3.UnitX * .01f, Goal],
            1 => [from],
            2 => [from, new(float.NaN), Goal],
            3 => Enumerable.Repeat(from, 512).Append(Goal).ToArray(),
            4 => [from, from + Vector3.UnitY, Goal],
            5 => [from, from],
            _ => [from, new(1500, 0, 0), Goal],
        }));
        Assert.False(coordinator.Select(100, Goal, Vector3.Zero, At(), 0, true).Ready);
    }

    [Fact]
    public void StartRoundoffIsNormalizedButCannotCollapseTheEntireRoute()
    {
        using var coordinator = new CompanionApproachCoordinator((from, _, _) =>
            Task.FromResult<IReadOnlyList<Vector3>>(Path(from + Vector3.UnitX * .001f)));
        var selected = coordinator.Select(100, Goal, Vector3.Zero, At(), 0, true);
        Assert.True(selected.Ready); Assert.Equal(At().Position, selected.Route[0]);
        using var collapsed = new CompanionApproachCoordinator((from, _, _) =>
            Task.FromResult<IReadOnlyList<Vector3>>([from + Vector3.UnitX * .0015f, from]));
        Assert.False(collapsed.Select(100, At().Position, Vector3.Zero, At(), 0, true).Ready);
    }

    [Fact]
    public void SelfCrossingRouteAlwaysRequestsExactZeroUntilExplicitlyAdopted()
    {
        var start = At().Position;
        using var coordinator = new CompanionApproachCoordinator((_, _, _) => Task.FromResult<IReadOnlyList<Vector3>>(
            [start, start + Vector3.UnitZ, start, Goal]));
        var selected = coordinator.Select(100, Goal, Vector3.Zero, At(), 0, true);
        var repeated = coordinator.Select(100, Goal, Vector3.Zero, At(), .1, true);
        Assert.Same(selected.Route, repeated.Route); Assert.Equal(0, repeated.RebaseArc);
        var motion = new CompanionRouteMotion(); motion.Update(OldRoute, 2, Intent, 0, true); motion.ResolvePlacement(true);
        Assert.True(motion.TryRebase(repeated.Route, repeated.RebaseArc!.Value));
        Assert.Equal(0, motion.Current.Arc);
        var adopted = coordinator.Select(100, Goal, Vector3.Zero, motion.Current, .2, true);
        Assert.Null(adopted.RebaseArc); Assert.True(adopted.Ready);
    }

    [Fact]
    public void AdoptedRouteCanMoveAwayFromQueryOriginAndArriveAtActualEndpoint()
    {
        var endpoint = Goal + Vector3.UnitX * .2f;
        using var coordinator = new CompanionApproachCoordinator((from, _, _) => Task.FromResult<IReadOnlyList<Vector3>>(Path(from, endpoint)));
        var selected = coordinator.Select(100, Goal, Vector3.Zero, At(), 0, true);
        var travelling = new CompanionRouteMotionFrame(selected.Route, 1, 1, PathTrail.At(selected.Route, 1), true, false);
        Assert.True(coordinator.Select(100, Goal, Vector3.Zero, travelling, .1, true).Ready);
        var arrived = travelling with { Arc = selected.ProgressArc, Position = endpoint };
        var stopped = coordinator.Select(100, Goal, Vector3.Zero, arrived, .2, true);
        Assert.True(stopped.AtDestination); Assert.True(stopped.HoldPosition); Assert.True(stopped.Ready);
    }

    [Fact]
    public void ProviderBuffersAreCopiedAndConsumerRouteMutationInvalidatesEvidence()
    {
        var returned = Path(At().Position);
        using var coordinator = new CompanionApproachCoordinator((_, _, _) => Task.FromResult<IReadOnlyList<Vector3>>(returned));
        var selected = coordinator.Select(100, Goal, Vector3.Zero, At(), 0, true);
        returned[1] = new(900, 0, 900);
        Assert.NotEqual(returned[1], selected.Route[1]);
        selected.Route[1] = new(800, 0, 800);
        Assert.False(coordinator.Select(100, Goal, Vector3.Zero, At(), .1, true).Ready);
    }

    [Fact]
    public void RetargetingIsThrottledAndSmallGoalJitterKeepsSameValidatedRoute()
    {
        var calls = 0;
        using var coordinator = new CompanionApproachCoordinator((from, to, _) =>
        { calls++; return Task.FromResult<IReadOnlyList<Vector3>>([from, to]); });
        var first = coordinator.Select(100, Goal, Vector3.Zero, At(), 0, true);
        Assert.Same(first.Route, coordinator.Select(100, Goal + Vector3.UnitX * .1f, Vector3.Zero, At(), 3, true).Route);
        Assert.Equal(1, calls);
        // First retarget can be issued after the interval; a further change cannot fan out immediately.
        var second = coordinator.Select(100, Goal + Vector3.UnitX * 2, Vector3.Zero, At(), 3.1, true);
        Assert.Equal(2, calls); Assert.NotSame(first.Route, second.Route);
        Assert.Same(second.Route, coordinator.Select(100, Goal + Vector3.UnitX * 4, Vector3.Zero, At(), 3.2, true).Route);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void CancelledUncooperativeProviderRetainsSingleFlightSlotAcrossRetargetAndReset()
    {
        var first = Pending(); var second = Pending(); var calls = 0; CancellationToken cancelled = default;
        using var coordinator = new CompanionApproachCoordinator((_, _, token) =>
        { calls++; if (calls == 1) { cancelled = token; return first.Task; } return second.Task; });
        coordinator.Select(100, Goal, Vector3.Zero, At(), 0, true);
        coordinator.Select(100, Goal + Vector3.UnitX, Vector3.Zero, At(), 2, true);
        Assert.True(cancelled.IsCancellationRequested); Assert.Equal(1, calls);
        for (var i = 3; i < 20; i++)
        { coordinator.Reset(); coordinator.Select(100, Goal, Vector3.Zero, At(), i, true); }
        Assert.Equal(1, calls); Assert.True(coordinator.QueryInFlight);
        first.SetResult(Path(At().Position));
        var waiting = coordinator.Select(100, Goal, Vector3.Zero, At(), 20, true);
        Assert.Equal(2, calls); Assert.False(waiting.Ready); Assert.True(coordinator.QueryInFlight);
        second.SetResult(Path(At().Position));
        Assert.True(coordinator.Select(100, Goal, Vector3.Zero, At(), 20.1, true).Ready);
    }

    [Fact]
    public void TimeoutCancelsWithoutSpawningAnotherQueryBehindTheProvidersBack()
    {
        var work = Pending(); var calls = 0; CancellationToken token = default;
        using var coordinator = new CompanionApproachCoordinator((_, _, t) => { calls++; token = t; return work.Task; });
        coordinator.Select(100, Goal, Vector3.Zero, At(), 0, true);
        coordinator.Select(100, Goal, Vector3.Zero, At(), 15, true);
        Assert.True(token.IsCancellationRequested);
        coordinator.Select(100, Goal, Vector3.Zero, At(), 100, true);
        Assert.Equal(1, calls); Assert.True(coordinator.QueryInFlight);
    }

    [Fact]
    public void ZoneChangeAndDisableRejectLateResults()
    {
        var work = Pending();
        using var coordinator = new CompanionApproachCoordinator((_, _, _) => work.Task);
        coordinator.Select(100, Goal, Vector3.Zero, At(), 0, true);
        coordinator.Select(101, Goal, Vector3.Zero, At(), .1, true);
        work.SetResult(Path(At().Position));
        Assert.False(coordinator.Select(101, Goal, Vector3.Zero, At(), .2, true).Ready);
        Assert.False(coordinator.Select(101, Goal, Vector3.Zero, At(), .3, false).Ready);
    }

    [Fact]
    public void ClockRollbackAndProviderFailureWaitBeforeRetrying()
    {
        var calls = 0;
        using var coordinator = new CompanionApproachCoordinator((_, _, _) =>
        { calls++; throw new InvalidOperationException("provider unavailable"); });
        Assert.False(coordinator.Select(100, Goal, Vector3.Zero, At(), 10, true).Ready);
        coordinator.Select(100, Goal, Vector3.Zero, At(), 10.1, true); Assert.Equal(1, calls);
        coordinator.Select(100, Goal, Vector3.Zero, At(), 1, true); Assert.Equal(1, calls);
        coordinator.Select(100, Goal, Vector3.Zero, At(), 2.9, true); Assert.Equal(1, calls);
        coordinator.Select(100, Goal, Vector3.Zero, At(), 3, true); Assert.Equal(2, calls);
    }

    [Fact]
    public void InvalidInputsNeverRequestANativeQuery()
    {
        var calls = 0;
        using var coordinator = new CompanionApproachCoordinator((_, _, _) => { calls++; return Pending().Task; });
        coordinator.Select(0, Goal, Vector3.Zero, At(), 0, true);
        coordinator.Select(100, new(float.NaN), Vector3.Zero, At(), 0, true);
        coordinator.Select(100, Goal, new(float.PositiveInfinity), At(), 0, true);
        coordinator.Select(100, Goal, Vector3.Zero, At() with { Arc = 99 }, 0, true);
        coordinator.Select(100, Goal, Vector3.Zero, At(), double.NaN, true);
        coordinator.Select(100, Goal, Vector3.Zero, At(), 0, false);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void CancellationCallbackExceptionsAndLateFaultsCannotEscapeDispose()
    {
        var work = Pending(); var calls = 0;
        var coordinator = new CompanionApproachCoordinator((_, _, token) =>
        { calls++; token.Register(() => throw new InvalidOperationException("callback")); return work.Task; });
        coordinator.Select(100, Goal, Vector3.Zero, At(), 0, true);
        coordinator.Dispose(); coordinator.Dispose();
        work.SetException(new InvalidOperationException("late provider failure"));
        Assert.False(coordinator.Select(100, Goal, Vector3.Zero, At(), 100, true).Ready); Assert.Equal(1, calls);
    }
}
