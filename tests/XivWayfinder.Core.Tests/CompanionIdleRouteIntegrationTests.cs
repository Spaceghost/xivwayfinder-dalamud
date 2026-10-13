using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionIdleRouteIntegrationTests
{
    private static readonly Vector3[] MainRoute = [Vector3.Zero, new(0, 0, 30)];
    private static readonly CompanionIntentFrame WalkIntent = new(CompanionIntentMode.Returning, 0, 0, true, 1.5f);

    private sealed class Walk : IDisposable
    {
        public CompanionIdleSnapshot Input = new(129, 1, Vector3.Zero, 0, 2, .9f, .4f, true, true);
        public readonly CompanionIdleRendezvous Idle = new();
        public readonly CompanionRouteMotion Motion = new();
        public readonly CompanionJumpTraversal Jump = new((_, _, _, _, _, _) => default, (_, _) => GuideJumpEvidence.Clear);
        public readonly CompanionApproachCoordinator Approach;
        private readonly CompanionTeleportMotion controller;
        public readonly List<(Vector3 From, Vector3 To, CancellationToken Cancel)> Queries = [];
        public double Now;

        public Walk(Func<Vector3, Vector3, CancellationToken, Task<IReadOnlyList<Vector3>>> query)
        {
            Motion.Update(MainRoute, 1, WalkIntent, 0, true);
            Motion.ResolvePlacement(true);
            Approach = new((from, to, cancel) =>
            { Queries.Add((from, to, cancel)); return query(from, to, cancel); });
            controller = new(Select);
        }

        private CompanionApproachSelection Select(uint zone, Vector3 goal, Vector3 player,
            CompanionRouteMotionFrame accepted, double now, bool enabled)
        {
            var route = Approach.Select(zone, goal, player, accepted, now, enabled);
            if (!route.Ready) return route;
            if (Idle.Current is { } request && Idle.AcceptsQueriedEndpoint(request, route.Route[^1], player)) return route;
            // Rejecting clearance must also discard the coordinator's cached
            // path. Its existing cooldown still bounds the next query.
            Approach.Reset();
            return new(accepted.Route, accepted.Arc, null, true, false, false);
        }

        public CompanionTeleportMotionFrame? Step(bool accept = true)
        {
            Now += .05;
            var request = Idle.Update(Input, Now);
            if (request is not { } goal)
            {
                Approach.Reset();
                Jump.ResolvePlacement(Motion, false);
                Motion.HoldJumpClock(Now);
                return null;
            }
            var cue = new CompanionTeleportFrame(CompanionTeleportMode.RequestStop, goal.Goal,
                Input.Player, goal.Generation, false, false, false);
            var frame = controller.Update(Motion, Jump, Input.Territory, Input.Player, cue, WalkIntent, default, Now, true);
            if (frame.MotionSubmitted) Jump.ResolvePlacement(Motion, accept);
            return frame;
        }

        public void Arrive()
        {
            for (var i = 0; i < 200; i++)
            {
                if (Step() is { AtDestination: true }) return;
            }
            Assert.Fail("Companion did not arrive at the validated idle endpoint.");
        }

        public void Dispose() => Approach.Dispose();
    }

    private static Task<IReadOnlyList<Vector3>> SidePath(Vector3 from, Vector3 to, CancellationToken _) =>
        Task.FromResult<IReadOnlyList<Vector3>>([from, new(to.X, from.Y, from.Z), to]);

    [Fact]
    public void IdleCompanionWalksEveryCornerFromAcceptedOriginWithoutChangingMainRoute()
    {
        using var walk = new Walk(SidePath);
        var initial = walk.Motion.Current.Position;
        var previous = initial;
        var sawSecondLeg = false;
        for (var i = 0; i < 180; i++)
        {
            var frame = walk.Step();
            var current = walk.Motion.Current;
            Assert.InRange(Vector3.Distance(previous, current.Position), 0, .076f);
            Assert.InRange(Vector3.Distance(PathTrail.At(current.Route, current.Arc), current.Position), 0, .00001f);
            if (!ReferenceEquals(current.Route, MainRoute))
            {
                Assert.Equal(initial, current.Route[0]);
                if (current.Position.Z < .95f)
                { sawSecondLeg = true; Assert.InRange(current.Position.X, -2.0001f, -1.9999f); }
            }
            previous = current.Position;
            if (frame is { AtDestination: true }) break;
        }
        Assert.True(sawSecondLeg);
        Assert.Equal(initial, Assert.Single(walk.Queries).From);
        Assert.InRange(Vector3.Distance(new(-2, 0, 0), walk.Motion.Current.Position), 0, .002f);
        Assert.True(walk.Idle.AcceptsQueriedEndpoint(walk.Idle.Current!.Value, walk.Motion.Current.Position, walk.Input.Player));
        Assert.Equal(new Vector3(0, 0, 30), MainRoute[^1]);
        Assert.NotSame(MainRoute, walk.Motion.Current.Route); // Never publish this arc as a main-route arc.
    }

    [Fact]
    public void InwardEndpointRetainsAcceptedPoseAndRetriesWithinExistingCooldown()
    {
        var attempts = 0;
        using var walk = new Walk((from, to, _) => Task.FromResult<IReadOnlyList<Vector3>>(
            ++attempts == 1 ? [from, new(-1.51f, 0, 0)] : [from, to]));
        var initial = walk.Motion.Current;
        while (walk.Queries.Count == 0) walk.Step();
        var firstAt = walk.Now;
        Assert.Equal(initial.Position, walk.Motion.Current.Position);
        Assert.Same(initial.Route, walk.Motion.Current.Route);
        while (walk.Now - firstAt < CompanionApproachCoordinator.MinRequestInterval - .1)
        {
            var result = walk.Step();
            Assert.False(result?.MotionSubmitted ?? false);
            Assert.Equal(initial.Position, walk.Motion.Current.Position);
            Assert.Single(walk.Queries);
        }
        walk.Arrive();
        Assert.Equal(2, walk.Queries.Count);
        Assert.Equal(initial.Position, walk.Queries[1].From);
        Assert.InRange(Vector3.Distance(new(-2, 0, 0), walk.Motion.Current.Position), 0, .002f);
    }

    [Fact]
    public void PlacementRefusalRollsBackIdleMotionWithoutBankingALaterLeap()
    {
        using var walk = new Walk(SidePath);
        while (walk.Idle.Current is null) walk.Step();
        var accepted = walk.Motion.Current.Position;
        for (var i = 0; i < 60; i++)
        {
            Assert.True(walk.Step(accept: false)?.MotionSubmitted);
            Assert.Equal(accepted, walk.Motion.Current.Position);
        }
        Assert.True(walk.Step()?.MotionSubmitted);
        Assert.InRange(Vector3.Distance(accepted, walk.Motion.Current.Position), .00001f, .011f);
        Assert.Single(walk.Queries);
    }

    [Fact]
    public void MovementCancelsPendingIdleQueryAndLateCompletionCannotAdoptItsRoute()
    {
        var result = new TaskCompletionSource<IReadOnlyList<Vector3>>();
        using var walk = new Walk((_, _, _) => result.Task);
        var accepted = walk.Motion.Current;
        while (walk.Queries.Count == 0) walk.Step();
        var request = Assert.IsType<CompanionIdleRequest>(walk.Idle.Current);
        var query = Assert.Single(walk.Queries);
        walk.Input = walk.Input with { Player = new(0, 0, .1f) };
        Assert.Null(walk.Step());
        Assert.True(query.Cancel.IsCancellationRequested);
        result.SetResult([query.From, query.To]);
        for (var i = 0; i < 8; i++)
        {
            Assert.Null(walk.Step());
            Assert.Equal(accepted.Position, walk.Motion.Current.Position);
            Assert.Same(accepted.Route, walk.Motion.Current.Route);
        }
        Assert.False(walk.Idle.AcceptsQueriedEndpoint(request, query.To, walk.Input.Player));
        Assert.Single(walk.Queries);
    }

    [Fact]
    public void ResumedPlayerMovementReturnsThroughQueriedConnectorWithoutSnappingToMainRoute()
    {
        using var walk = new Walk(SidePath); walk.Arrive();
        var aside = walk.Motion.Current.Position;
        walk.Input = walk.Input with { Player = new(0, 0, .2f), Waiting = false };
        Assert.Null(walk.Step()); Assert.Equal(aside, walk.Motion.Current.Position);
        var returnQuery = new TaskCompletionSource<IReadOnlyList<Vector3>>();
        Vector3? queriedFrom = null, queriedTo = null;
        using var connector = new GuideConnectorCoordinator((from, to, _) =>
        { queriedFrom = from; queriedTo = to; return returnQuery.Task; });
        var lead = new CompanionIntentFrame(CompanionIntentMode.Lead, 4, 0, true);

        void Continue()
        {
            walk.Now += .05;
            var selected = connector.Select(129, MainRoute, .2f, lead, walk.Motion.Current, walk.Now, true);
            Assert.True(selected.RebaseArc is not { } arc || walk.Motion.TryRebase(selected.Route, arc));
            walk.Jump.Update(walk.Motion, 129, selected.Route, selected.ProgressArc, selected.Intent, default, walk.Now, true);
            walk.Jump.ResolvePlacement(walk.Motion, true);
        }

        Continue(); // Detect the old idle route's separation; this cannot rebase across two yalms.
        Assert.True(walk.Motion.Current.NeedsConnection);
        Assert.Equal(aside, walk.Motion.Current.Position);
        Continue();
        Assert.Equal(aside, queriedFrom);
        Assert.Equal(Vector3.Zero, queriedTo);
        for (var i = 0; i < 10; i++) { Continue(); Assert.Equal(aside, walk.Motion.Current.Position); }
        // The returned path walks around a corner; no synthesized direct leg.
        returnQuery.SetResult([aside, new(aside.X, 0, -1), new(0, 0, -1), Vector3.Zero]);
        var previous = aside;
        var sawDetour = false;
        for (var i = 0; i < 200; i++)
        {
            Continue();
            var current = walk.Motion.Current;
            Assert.InRange(Vector3.Distance(previous, current.Position), 0, .46f);
            Assert.InRange(Vector3.Distance(PathTrail.At(current.Route, current.Arc), current.Position), 0, .00001f);
            sawDetour |= current.Position.Z < -.5f;
            previous = current.Position;
            if (ReferenceEquals(current.Route, MainRoute)) break;
        }
        Assert.True(sawDetour);
        Assert.Same(MainRoute, walk.Motion.Current.Route);
        Assert.False(walk.Motion.Current.NeedsConnection);
        Assert.True(walk.Motion.Current.Arc > 0);
    }
}
