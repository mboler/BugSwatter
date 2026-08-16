namespace Informant.Tests;

public sealed class ReviewTimeBudgetTests
{
    [Fact]
    public void UnboundedBudgetNeverExpires()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));
        var budget = new ReviewTimeBudget(null, timeProvider);

        timeProvider.Advance(TimeSpan.FromDays(30));

        Assert.Null(budget.ConfiguredMinutes);
        Assert.Null(budget.DeadlineUtc);
        Assert.Null(budget.Remaining);
        Assert.False(budget.IsExhausted);
    }

    [Fact]
    public void BoundedBudgetUsesInjectedTimeProvider()
    {
        var startedAt = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(startedAt);
        var budget = new ReviewTimeBudget(15, timeProvider);

        timeProvider.Advance(TimeSpan.FromMinutes(14));
        Assert.False(budget.IsExhausted);
        Assert.Equal(TimeSpan.FromMinutes(1), budget.Remaining);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        Assert.True(budget.IsExhausted);
        Assert.Equal(TimeSpan.Zero, budget.Remaining);
        Assert.True(budget.CausedCancellation(CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveBudgetIsRejected(int configuredMinutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReviewTimeBudget(configuredMinutes));
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
