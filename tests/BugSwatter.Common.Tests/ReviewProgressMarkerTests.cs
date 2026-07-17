namespace BugSwatter.Common.Tests;

public sealed class ReviewProgressMarkerTests
{
    [Fact]
    public void SnapshotRoundTripsThroughTheStdoutMarker()
    {
        var expected = new ReviewProgressSnapshot
        {
            Phase = "Primary review",
            ModelName = "local-model",
            ModelProfile = "primary",
            CurrentFile = "src/Worker.cs",
            FileIndex = 3,
            FileCount = 12,
            ModelRequestActive = true,
            ModelRequestStartedUtc = DateTimeOffset.Parse("2026-07-13T08:00:00Z"),
            RunUsage = new ReviewUsageSnapshot { RequestCount = 7, PromptTokens = 1200, CompletionTokens = 300, TotalTokens = 1500, EstimatedCost = 0.0075m },
            CurrentUsage = new ReviewUsageSnapshot { RequestCount = 2, TotalTokens = 400 },
            LocalUsage = new ReviewUsageSnapshot { RequestCount = 5, TotalTokens = 1100 },
            FrontierUsage = new ReviewUsageSnapshot { RequestCount = 2, TotalTokens = 400, EstimatedCost = 0.0075m }
        };

        string line = ReviewProgressMarker.Format(expected);
        bool parsed = ReviewProgressMarker.TryParse(line, out ReviewProgressSnapshot? actual);

        Assert.True(parsed);
        Assert.Equal(expected, actual);
        Assert.StartsWith("INFORMANT-PROGRESS: {", line);
    }

    [Theory]
    [InlineData("ordinary log output")]
    [InlineData("INFORMANT-PROGRESS: not-json")]
    [InlineData("INFORMANT-PROGRESS: {}")]
    [InlineData("INFORMANT-PROGRESS: {\"version\":3,\"phase\":\"future\"}")]
    [InlineData("INFORMANT-PROGRESS: {\"version\":1,\"phase\":\"old\"}")]
    [InlineData("INFORMANT-PROGRESS: {\"version\":2,\"phase\":\"\"}")]
    [InlineData("INFORMANT-PROGRESS: {\"version\":2,\"phase\":\"review\",\"runUsage\":{\"requestCount\":-1}}")]
    [InlineData("INFORMANT-PROGRESS: {\"version\":2,\"phase\":\"review\",\"frontierUsage\":{\"requestCount\":1,\"estimatedCost\":-1}}")]
    [InlineData("INFORMANT-PROGRESS: {\"version\":2,\"phase\":\"review\",\"modelRequestActive\":true}")]
    [InlineData("INFORMANT-PROGRESS: {\"version\":2,\"phase\":\"review\",\"fileIndex\":2,\"fileCount\":1}")]
    public void InvalidOrUnrelatedLinesAreRejected(string line)
    {
        Assert.False(ReviewProgressMarker.TryParse(line, out ReviewProgressSnapshot? snapshot));
        Assert.Null(snapshot);
    }
}
