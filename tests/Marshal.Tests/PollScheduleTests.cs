namespace Marshal.Tests;

public sealed class PollScheduleTests
{
    public static TheoryData<string, DateTimeOffset, DateTimeOffset> CronExamples => new()
    {
        { "0 */5 * * * *", Utc(2026, 7, 12, 12, 1), Utc(2026, 7, 12, 12, 5) },
        { "*/15 * * * *", Utc(2026, 7, 12, 12, 1), Utc(2026, 7, 12, 12, 15) },
        { "0 30 9 * * Mon-Fri", Utc(2026, 7, 12, 12, 0), Utc(2026, 7, 13, 9, 30) },
        { "0 0 2 * * Sun", Utc(2026, 7, 12, 2, 0), Utc(2026, 7, 19, 2, 0) },
        { "0 0 2 1 * *", Utc(2026, 7, 12, 12, 0), Utc(2026, 8, 1, 2, 0) },
        { "0 0 0 1 Jan *", Utc(2026, 7, 12, 12, 0), Utc(2027, 1, 1, 0, 0) }
    };

    public static TheoryData<string> InvalidExpressions => new()
    {
        "",
        "   ",
        "not a schedule",
        "* * * *",
        "* * * * * * *",
        "1 * * * * *",
        "*/30 * * * * *",
        "0 61 * * * *",
        "0 0 0 31 Feb *",
        "00:00:59",
        "-00:05:00"
    };

    [Theory]
    [MemberData(nameof(CronExamples))]
    public void CalculatesExpectedCronOccurrence(string expression, DateTimeOffset now, DateTimeOffset expected)
    {
        PollSchedule schedule = PollSchedule.Parse(expression);

        Assert.False(schedule.IsInterval);
        Assert.Equal(expected, schedule.GetNextOccurrence(now));
    }

    [Theory]
    [InlineData("00:01:00", 1)]
    [InlineData("00:05:00", 5)]
    [InlineData("7.00:00:00", 10080)]
    [InlineData("30.00:00:00", 43200)]
    public void CalculatesExpectedFixedInterval(string expression, int expectedMinutes)
    {
        DateTimeOffset now = Utc(2026, 7, 12, 12, 0);
        PollSchedule schedule = PollSchedule.Parse(expression);

        Assert.True(schedule.IsInterval);
        Assert.Equal(now.AddMinutes(expectedMinutes), schedule.GetNextOccurrence(now));
    }

    [Fact]
    public void CronOccurrenceIsStrictlyAfterCurrentBoundary()
    {
        DateTimeOffset boundary = Utc(2026, 7, 12, 12, 5);

        Assert.Equal(Utc(2026, 7, 12, 12, 10), PollSchedule.Parse("0 */5 * * * *").GetNextOccurrence(boundary));
    }

    [Theory]
    [MemberData(nameof(InvalidExpressions))]
    public void RejectsInvalidOrTooFrequentExpression(string expression)
    {
        Assert.Throws<FormatException>(() => PollSchedule.Parse(expression));
    }

    [Fact]
    public void PreservesTrimmedExpression()
    {
        Assert.Equal("0 */5 * * * *", PollSchedule.Parse("  0 */5 * * * *  ").Expression);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
