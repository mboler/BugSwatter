namespace BugSwatter.Common.Tests;

public sealed class TextSummaryTests
{
    [Fact]
    public void ShortTextIsTrimmedWithoutMarker()
    {
        Assert.Equal("short text", TextSummary.Create("  short text\r\n", 20));
    }

    [Fact]
    public void LongTextIsBoundedAndMarked()
    {
        Assert.Equal("01234 [truncated]", TextSummary.Create("0123456789", 5));
    }

    [Fact]
    public void NonPositiveLimitIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextSummary.Create("text", 0));
    }
}
