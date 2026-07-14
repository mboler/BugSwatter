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
            ModelRequestCount = 7,
            PromptTokens = 1200,
            CompletionTokens = 300,
            TotalTokens = 1500
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
    [InlineData("INFORMANT-PROGRESS: {\"version\":2,\"phase\":\"future\",\"modelRequestCount\":0}")]
    [InlineData("INFORMANT-PROGRESS: {\"version\":1,\"phase\":\"\",\"modelRequestCount\":0}")]
    [InlineData("INFORMANT-PROGRESS: {\"version\":1,\"phase\":\"review\",\"modelRequestCount\":-1}")]
    [InlineData("INFORMANT-PROGRESS: {\"version\":1,\"phase\":\"review\",\"modelRequestActive\":true,\"modelRequestCount\":1}")]
    [InlineData("INFORMANT-PROGRESS: {\"version\":1,\"phase\":\"review\",\"fileIndex\":2,\"fileCount\":1,\"modelRequestCount\":0}")]
    public void InvalidOrUnrelatedLinesAreRejected(string line)
    {
        Assert.False(ReviewProgressMarker.TryParse(line, out ReviewProgressSnapshot? snapshot));
        Assert.Null(snapshot);
    }
}
