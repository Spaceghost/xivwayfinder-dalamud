using System.Numerics;

namespace XivWayfinder.Core.Tests;

public sealed class CompanionIdleRendezvousTests
{
    private static CompanionIdleSnapshot Input => new(129, 1, Vector3.Zero, 0, 2, .771358f, .4f, true, true);

    private static CompanionIdleRequest Settle(CompanionIdleRendezvous idle, CompanionIdleSnapshot input, double start = 0)
    {
        CompanionIdleRequest? result = null;
        for (var i = 0; i <= 20; i++) result = idle.Update(input, start + i * .05);
        return Assert.IsType<CompanionIdleRequest>(result);
    }

    [Fact]
    public void RequiresStableWaitingBeforeRequestingSavedSide()
    {
        var idle = new CompanionIdleRendezvous();
        for (var i = 0; i < 18; i++) Assert.Null(idle.Update(Input, i * .05));
        var request = Assert.IsType<CompanionIdleRequest>(idle.Update(Input, .91));
        Assert.Equal(new Vector3(-2, 0, 0), request.Goal);
        Assert.Equal(Input.Player, request.PlayerAnchor);
        Assert.Equal(129u, request.Territory);
        Assert.Equal(Input.Context, request.Context);
        Assert.InRange(request.MinimumClearance, 1.52135f, 1.52137f);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(0, -2)]
    [InlineData(1.5707963f, 2)]
    [InlineData(-1.5707963f, -2)]
    public void PositiveAndNegativeSidesMatchExistingBesideConvention(float yaw, float side)
    {
        var input = Input with { Player = new(14, 7, 25), PlayerYaw = yaw, Side = side };
        var request = Settle(new(), input);
        var expected = MinionGlove.PlaceBeside(input.Player, yaw, new(0, side, 0, 0, 0), 0);
        Assert.NotNull(expected);
        Assert.InRange(Vector3.Distance(expected.Value, request.Goal), 0, .00001f);
    }

    [Fact]
    public void TurningInPlaceKeepsCapturedHeadingAndQueryIdentity()
    {
        var idle = new CompanionIdleRendezvous();
        idle.Update(Input, 0);
        var request = Settle(idle, Input with { PlayerYaw = MathF.PI }, .05);
        Assert.Equal(new Vector3(-2, 0, 0), request.Goal);
        for (var i = 1; i <= 80; i++)
            Assert.Equal(request, idle.Update(Input with { PlayerYaw = i * .2f }, 1.05 + i * .05));
    }

    [Fact]
    public void MovementCancelsImmediatelyAndNewRestMustSettleAgain()
    {
        var idle = new CompanionIdleRendezvous(); var before = Settle(idle, Input);
        var moved = Input with { Player = new(.03f, 0, 0), PlayerYaw = MathF.PI / 2 };
        Assert.Null(idle.Update(moved, 1.05));
        Assert.False(idle.AcceptsQueriedEndpoint(before, before.Goal, moved.Player));
        for (var i = 1; i <= 17; i++) Assert.Null(idle.Update(moved, 1.05 + i * .05));
        var after = Assert.IsType<CompanionIdleRequest>(idle.Update(moved, 2));
        Assert.True(after.Generation > before.Generation);
        Assert.InRange(Vector3.Distance(new(.03f, 0, 2), after.Goal), 0, .00001f);
    }

    [Fact]
    public void SlowCumulativeMovementCannotMasqueradeAsStationaryFrames()
    {
        var idle = new CompanionIdleRendezvous();
        for (var i = 0; i <= 600; i++)
            Assert.Null(idle.Update(Input with { Player = new(i * .001f, 0, 0) }, i / 60.0));
    }

    [Fact]
    public void SmallJitterNeverChasesANewOffset()
    {
        var idle = new CompanionIdleRendezvous(); var request = Settle(idle, Input);
        for (var i = 1; i <= 60; i++)
        {
            var player = new Vector3(MathF.Sin(i) * .005f, 0, 0);
            Assert.Equal(request, idle.Update(Input with { Player = player }, 1 + i * .05));
        }
    }

    [Fact]
    public void LeavingWaitingYieldsWithoutARepositionRequest()
    {
        var idle = new CompanionIdleRendezvous(); var before = Settle(idle, Input);
        Assert.Null(idle.Update(Input with { Waiting = false }, 1.05));
        Assert.False(idle.AcceptsQueriedEndpoint(before, before.Goal, Input.Player));
        Assert.Null(idle.Update(Input, 1.1));
    }

    [Fact]
    public void CallerMovementOwnershipGateInvalidatesOutstandingQuery()
    {
        // The plugin must map jumps, teleport/boarding, unfinished stand-up,
        // hidden guidance and missing routes to CanReposition=false.
        var idle = new CompanionIdleRendezvous(); var before = Settle(idle, Input);
        Assert.Null(idle.Update(Input with { CanReposition = false }, 1.05));
        Assert.False(idle.AcceptsQueriedEndpoint(before, before.Goal, Input.Player));
        var after = Settle(idle, Input, 1.1);
        Assert.Equal(before.Goal, after.Goal);
        Assert.True(after.Generation > before.Generation);
        Assert.False(idle.AcceptsQueriedEndpoint(before, before.Goal, Input.Player));
    }

    [Fact]
    public void ResetInvalidatesEvenAnIdenticalLaterDestination()
    {
        var idle = new CompanionIdleRendezvous(); var before = Settle(idle, Input);
        idle.Reset(); Assert.Null(idle.Current);
        var after = Settle(idle, Input, 2);
        Assert.True(after.Generation > before.Generation);
        Assert.False(idle.AcceptsQueriedEndpoint(before, before.Goal, Input.Player));
        Assert.True(idle.AcceptsQueriedEndpoint(after, after.Goal, Input.Player));
    }

    [Theory]
    [InlineData(0)] // territory
    [InlineData(1)] // route/destination/appearance revision
    [InlineData(2)] // saved side
    [InlineData(3)] // guide size/envelope
    [InlineData(4)] // player radius
    public void ContextAndSizeChangesInvalidateOldQuery(int change)
    {
        var idle = new CompanionIdleRendezvous(); var before = Settle(idle, Input);
        var changed = change switch
        {
            0 => Input with { Territory = 130 },
            1 => Input with { Context = 2 },
            2 => Input with { Side = -2 },
            3 => Input with { GuideRadius = 1.2f },
            _ => Input with { PlayerRadius = .6f },
        };
        Assert.Null(idle.Update(changed, 1.05));
        Assert.False(idle.AcceptsQueriedEndpoint(before, before.Goal, Input.Player));
        var after = Settle(idle, changed, 1.1);
        Assert.True(after.Generation > before.Generation);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void ClockReversalOrSuspensionCannotBankStableTime(double nextTime)
    {
        var idle = new CompanionIdleRendezvous(); var before = Settle(idle, Input);
        Assert.Null(idle.Update(Input, nextTime));
        Assert.False(idle.AcceptsQueriedEndpoint(before, before.Goal, Input.Player));
        Assert.Null(idle.Update(Input, nextTime + .1));
    }

    [Fact]
    public void ClearanceExpandsForLargeGuidesWithoutReducingRequestedSafety()
    {
        var input = Input with { Side = -.1f, GuideRadius = 2.5f, PlayerRadius = .6f };
        var request = Settle(new(), input);
        Assert.InRange(request.MinimumClearance, 3.44999f, 3.45001f);
        Assert.InRange(request.Goal.X, 3.44999f, 3.45001f);
        Assert.Equal(0, request.Goal.Z);
    }

    [Fact]
    public void ZeroSideStillRequestsClearanceOnDefaultSide()
    {
        var request = Settle(new(), Input with { Side = 0 });
        Assert.Equal(-request.MinimumClearance, request.Goal.X);
    }

    [Theory]
    [InlineData(0)] // unknown guide radius
    [InlineData(1)] // malformed player radius
    [InlineData(2)] // nonfinite side
    [InlineData(3)] // nonfinite position
    [InlineData(4)] // nonfinite heading
    [InlineData(5)] // unavailable territory
    [InlineData(6)] // impossible stand-off: must not clamp into an oversized body
    public void InvalidOrUnboundedInputsNeverRequestPlacement(int variant)
    {
        var input = variant switch
        {
            0 => Input with { GuideRadius = 0 },
            1 => Input with { PlayerRadius = float.NaN },
            2 => Input with { Side = float.PositiveInfinity },
            3 => Input with { Player = new(float.NaN) },
            4 => Input with { PlayerYaw = float.NaN },
            5 => Input with { Territory = 0 },
            _ => Input with { GuideRadius = 20 },
        };
        var idle = new CompanionIdleRendezvous(); var before = Settle(idle, Input);
        Assert.Null(idle.Update(input, 1.05));
        Assert.False(idle.AcceptsQueriedEndpoint(before, before.Goal, Input.Player));
    }

    [Fact]
    public void NearbyNavmeshEndpointMustRetainBodyClearanceAndHeightNeighborhood()
    {
        var idle = new CompanionIdleRendezvous(); var request = Settle(idle, Input);
        Assert.True(idle.AcceptsQueriedEndpoint(request, new(-1.8f, .2f, .1f), Input.Player));
        // The approach router permits this 0.49-yalm inward endpoint error,
        // but it puts this Fox inside the required player/model envelope gap.
        Assert.False(idle.AcceptsQueriedEndpoint(request, new(-1.51f, 0, 0), Input.Player));
        Assert.False(idle.AcceptsQueriedEndpoint(request, new(-2.51f, 0, 0), Input.Player));
        Assert.False(idle.AcceptsQueriedEndpoint(request, new(-2, .501f, 0), Input.Player));
        Assert.False(idle.AcceptsQueriedEndpoint(request, new(float.NaN), Input.Player));
        Assert.False(idle.AcceptsQueriedEndpoint(request, request.Goal, new(.03f, 0, 0)));
    }

    [Fact]
    public void EndpointClearanceUsesCurrentPlayerWithinAllowedJitter()
    {
        var idle = new CompanionIdleRendezvous(); var request = Settle(idle, Input with { Side = 0 });
        Assert.True(idle.AcceptsQueriedEndpoint(request, request.Goal, Vector3.Zero));
        Assert.False(idle.AcceptsQueriedEndpoint(request, request.Goal, new(-.01f, 0, 0)));
    }

    [Fact]
    public void CompleteApproachRouteCanStillEndTooCloseToThePlayer()
    {
        var idle = new CompanionIdleRendezvous(); var request = Settle(idle, Input);
        Vector3[] owned = [new(0, 0, 3), new(0, 0, 4)];
        var accepted = new CompanionRouteMotionFrame(owned, 0, 0, owned[0], true, false);
        using var routing = new CompanionApproachCoordinator((from, _, _) =>
            Task.FromResult<IReadOnlyList<Vector3>>([from, new(-1.51f, 0, 0)]));
        var queried = routing.Select(Input.Territory, request.Goal, Input.Player, accepted, 2, true);
        Assert.True(queried.Ready); // Complete path, inside the router's endpoint tolerance.
        Assert.Equal(accepted.Position, queried.Route[0]);
        Assert.False(idle.AcceptsQueriedEndpoint(request, queried.Route[^1], Input.Player));
        Assert.Equal(request, idle.Current); // A rejected endpoint does not invent a replacement offset.
    }
}
