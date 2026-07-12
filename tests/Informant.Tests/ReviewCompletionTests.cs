namespace Informant.Tests;

public sealed class ReviewCompletionTests
{
    [Fact]
    public void CompletedAndExpectedExclusionsAllowBaselineAdvancement()
    {
        FileReviewResult[] results =
        [
            Result(FileReviewStatus.Reviewed),
            Result(FileReviewStatus.NotReviewable)
        ];

        Assert.True(ReviewCompletion.CanAdvanceBaseline(results));
    }

    [Theory]
    [InlineData(FileReviewStatus.Failed)]
    [InlineData(FileReviewStatus.Partial)]
    public void IncompleteReviewPreventsBaselineAdvancement(FileReviewStatus status)
    {
        FileReviewResult[] results =
        [
            Result(FileReviewStatus.Reviewed),
            Result(status)
        ];

        Assert.False(ReviewCompletion.CanAdvanceBaseline(results));
    }

    private static FileReviewResult Result(FileReviewStatus status)
    {
        string? findings = status is FileReviewStatus.Reviewed or FileReviewStatus.Partial ? "findings" : null;
        string? reason = status is FileReviewStatus.Failed or FileReviewStatus.Partial ? "model call failed" : status == FileReviewStatus.NotReviewable ? "binary file" : null;
        return new FileReviewResult(new ChangedFile($"{status}.cs", ChangeKind.Modified, [new LineRange(1, 1)]), status, findings, status == FileReviewStatus.Partial ? 1 : 0, 2, reason);
    }
}
