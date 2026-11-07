namespace XivWayfinder.Core.Tests;

public sealed class GuideBoardingQueryBudgetTests
{
    [Fact]
    public void ManagedPreparationDoesNotPretendToConsumeNativeQueryTime()
    {
        var budget = new GuideBoardingQueryBudget(1000000);
        long clock = 8000; // Includes >3 ms of managed preparation before floor.
        for (var i = 0; i < 108; i++)
        {
            clock += 8000; // Even expensive inter-query managed work is separate.
            Assert.True(budget.TryBegin(clock));
            clock += 20;
            Assert.True(budget.Complete(clock));
        }
        Assert.Equal(108, budget.Queries);
        Assert.InRange(budget.QuerySeconds, .0021599, .0021601);
    }

    [Fact]
    public void NativeBudgetIsCumulativeAndCannotApproveAnOverBudgetFinalQuery()
    {
        var budget = new GuideBoardingQueryBudget(1000000);
        Assert.True(budget.TryBegin(0)); Assert.True(budget.Complete(1500));
        Assert.True(budget.TryBegin(10000)); Assert.False(budget.Complete(11500));
        Assert.False(budget.Available); Assert.False(budget.TryBegin(20000));
    }

    [Fact]
    public void QueryCountStillBoundsZeroCostClockSamples()
    {
        var budget = new GuideBoardingQueryBudget(1000000);
        for (var i = 0; i < 192; i++)
        { Assert.True(budget.TryBegin(i)); Assert.True(budget.Complete(i)); }
        Assert.Equal(192, budget.Queries); Assert.False(budget.TryBegin(999));
    }

    [Fact]
    public void InvalidClockAndOverlappingCallsFailClosed()
    {
        var budget = new GuideBoardingQueryBudget(1000000);
        Assert.False(budget.TryBegin(-1));
        Assert.True(budget.TryBegin(10)); Assert.False(budget.TryBegin(10));
        Assert.False(budget.Complete(9)); Assert.False(budget.TryBegin(20));
        Assert.False(new GuideBoardingQueryBudget(0).TryBegin(1));
        Assert.False(new GuideBoardingQueryBudget(1, maximumSeconds: double.NaN).TryBegin(1));
        Assert.False(new GuideBoardingQueryBudget(1).Complete(1));
    }
}
