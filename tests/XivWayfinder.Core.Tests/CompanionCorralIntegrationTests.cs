using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionCorralIntegrationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void RealPlannerRetargetsCatchRunnerThenStopReverseAndSettleWithoutQueryChurn(int fps)
    {
        var planner = new CompanionTeleportIntent();
        var motion = new CompanionRouteMotion();
        var jump = new CompanionJumpTraversal((_, _, _, _, _, _) => default, (_, _) => GuideJumpEvidence.Clear);
        var initial = new CompanionIntentFrame(CompanionIntentMode.Returning, 1, 0, true, 3.5f);
        jump.Update(motion, 100, [Vector3.Zero, new(300, 0, 0)], 0, initial, default, 0, true);
        jump.ResolvePlacement(motion, true);
        TaskCompletionSource<IReadOnlyList<Vector3>>? pending = null;
        Vector3 queryStart = default, queryGoal = default;
        double now = 0, completeAt = 0;
        var queryCount = 0;
        using var approach = new CompanionApproachCoordinator((from, goal, _) =>
        {
            Assert.Null(pending); queryCount++; queryStart = from; queryGoal = goal; completeAt = now + .1;
            pending = new(); return pending.Task;
        });
        var controller = new CompanionTeleportMotion((zone, goal, player, accepted, time, enabled) =>
            planner.Current.Mode == CompanionTeleportMode.Corral
                ? approach.SelectCorral(zone, goal, player, accepted, time, enabled)
                : approach.Select(zone, goal, player, accepted, time, enabled));
        var forward = new List<float>(); var reverse = new List<float>();
        var stationaryQueries = -1;
        var firstStopMaximum = 0f; var secondStopMaximum = 0f;
        for (var i = 0; i <= fps * 52; ++i)
        {
            now = (double)i / fps;
            if (pending is { } complete && now >= completeAt - .0000001)
            { pending = null; complete.SetResult([queryStart, queryGoal]); }
            var x = now < 20 ? (float)now * 6 : now < 26 ? 120 : now < 46 ? 120 - ((float)now - 26) * 6 : 0;
            var player = new Vector3(x, 0, 0);
            var cue = planner.Update(new(100, 8, player, MathF.PI / 2, motion.Current.Position, true, true), now);
            var intent = initial with { CruiseSpeed = planner.ApproachCruiseSpeed };
            var before = motion.Current;
            var result = controller.Update(motion, jump, 100, player, cue, intent, default, now, true);
            if (result.MotionSubmitted) jump.ResolvePlacement(motion, true);
            Assert.InRange(Vector3.Distance(before.Position, motion.Current.Position), 0, 9f / fps + .001f);
            if (pending is not null) Assert.Equal(queryStart, motion.Current.Position);
            if (now is > 10 and < 20) forward.Add(motion.Current.Position.X - x);
            if (now is > 36 and < 46) reverse.Add(x - motion.Current.Position.X);
            if (now is >= 20 and < 26) firstStopMaximum = Math.Max(firstStopMaximum, Heading.FlatDistance(player, motion.Current.Position));
            if (now >= 46) secondStopMaximum = Math.Max(secondStopMaximum, Heading.FlatDistance(player, motion.Current.Position));
            if (now >= 50)
            {
                if (stationaryQueries < 0) stationaryQueries = queryCount;
                Assert.Equal(stationaryQueries, queryCount);
                Assert.Equal(CompanionTeleportMode.RequestStop, cue.Mode);
                Assert.InRange(Heading.FlatDistance(player, motion.Current.Position), 1, 2);
                Assert.InRange(Math.Abs(motion.Current.Velocity), 0, .02f);
            }
        }
        Assert.InRange(queryCount, 20, 75);
        foreach (var samples in new[] { forward, reverse })
        {
            Assert.InRange(samples.Average(), .5f, 2);
            Assert.True(samples.Count(x => x >= 0) >= samples.Count * .95,
                "The independently walking guide must actually catch and stay ahead of the runner.");
            Assert.InRange(samples.Max(), 0, 3);
        }
        Assert.InRange(firstStopMaximum, 0, 4);
        Assert.InRange(secondStopMaximum, 0, 4);
        output.WriteLine($"fps={fps}, production cruise: forward={forward.Min():F3}..{forward.Max():F3} " +
            $"mean{forward.Average():F3}; reverse={reverse.Min():F3}..{reverse.Max():F3} mean{reverse.Average():F3}; " +
            $"stop max separation={firstStopMaximum:F3}/{secondStopMaximum:F3}; queries={queryCount}");
    }

    [Fact]
    public void CadenceIsCorralOnlyAndFailuresKeepTwoSecondRetryBound()
    {
        var route = new[] { Vector3.Zero, new Vector3(100, 0, 0) };
        var accepted = new CompanionRouteMotionFrame(route, 0, 0, Vector3.Zero, true, false);
        var calls = 0;
        using var normal = new CompanionApproachCoordinator((from, to, _) =>
        { calls++; return Task.FromResult<IReadOnlyList<Vector3>>([from, to]); });
        normal.Select(100, new(8, 0, 0), default, accepted, 0, true);
        normal.Select(100, new(12, 0, 0), default, accepted, .75, true); Assert.Equal(1, calls);
        normal.Select(100, new(12, 0, 0), default, accepted, 2, true); Assert.Equal(2, calls);
        calls = 0;
        using var corral = new CompanionApproachCoordinator((from, to, _) =>
        { calls++; return Task.FromResult<IReadOnlyList<Vector3>>([from, to]); });
        corral.SelectCorral(100, new(8, 0, 0), default, accepted, 0, true);
        corral.SelectCorral(100, new(12, 0, 0), default, accepted, .749, true); Assert.Equal(1, calls);
        corral.SelectCorral(100, new(12, 0, 0), default, accepted, .75, true); Assert.Equal(1, calls);
        corral.SelectCorral(100, new(13, 0, 0), default, accepted, .8, true); Assert.Equal(2, calls);
        calls = 0;
        using var failed = new CompanionApproachCoordinator((_, _, _) =>
        { calls++; throw new InvalidOperationException(); });
        foreach (var now in new[] { 0, .75, 1.5, 1.999 })
            failed.SelectCorral(100, new((float)now + 8, 0, 0), default, accepted, now, true);
        Assert.Equal(1, calls);
        failed.SelectCorral(100, new(12, 0, 0), default, accepted, 2, true); Assert.Equal(2, calls);
    }

    [Fact]
    public void ExactReversalChangesAnticipationDirectionAndStoppingRemovesTheLongOffset()
    {
        var planner = new CompanionTeleportIntent();
        CompanionTeleportFrame cue = default;
        for (var i = 0; i <= 240; ++i)
        {
            var now = i / 60d;
            var player = new Vector3(i <= 120 ? (float)now * 6 : 12 - ((float)now - 2) * 6, 0, 0);
            cue = planner.Update(new(100, 8, player, MathF.PI / 2, null, true, true), now);
            if (i == 120) Assert.True(cue.Rendezvous!.Value.X > player.X);
        }
        Assert.True(cue.Rendezvous!.Value.X < 0);
        for (var i = 241; i <= 420; ++i)
            cue = planner.Update(new(100, 8, Vector3.Zero, 0, null, true, true), i / 60d);
        Assert.Equal(CompanionTeleportMode.RequestStop, cue.Mode);
        Assert.InRange(Heading.FlatDistance(Vector3.Zero, cue.Rendezvous!.Value), 1.6f, 1.8f);
        var stopped = cue;
        for (var i = 421; i <= 600; ++i)
            Assert.Equal(stopped.Revision, planner.Update(new(100, 8, Vector3.Zero, i, null, true, true), i / 60d).Revision);
    }
}
