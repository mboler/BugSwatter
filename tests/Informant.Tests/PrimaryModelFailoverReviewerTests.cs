namespace Informant.Tests;

internal sealed class StubPrimaryModelSession : IPrimaryModelSession
{
    private readonly Queue<FileReviewResult> _results;
    private readonly Queue<string> _planningResults = [];
    private readonly Queue<ReviewUnitResult> _unitResults = [];

    public StubPrimaryModelSession(PrimaryModelTarget target, VerificationResult verification, params FileReviewResult[] results)
    {
        Target = target;
        Verification = verification;
        _results = new Queue<FileReviewResult>(results);
    }

    public PrimaryModelTarget Target { get; }

    public VerificationResult Verification { get; }

    public int VerificationCount { get; private set; }

    public int ReviewCount { get; private set; }

    public int PlanningCount { get; private set; }

    public int UnitReviewCount { get; private set; }

    public bool CancelReview { get; set; }

    public bool FailPlanning { get; set; }

    public void EnqueuePlanningResult(string result) => _planningResults.Enqueue(result);

    public void EnqueueUnitResult(ReviewUnitResult result) => _unitResults.Enqueue(result);

    public Task<VerificationResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        VerificationCount++;
        return Task.FromResult(Verification);
    }

    public Task<FileReviewResult> ReviewAsync(ChangedFile file, CancellationToken cancellationToken = default)
    {
        ReviewCount++;
        if (CancelReview)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return Task.FromResult(_results.Dequeue());
    }

    public Task<string> PlanAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        PlanningCount++;
        return FailPlanning
            ? Task.FromException<string>(new ModelCallException("planning endpoint failed"))
            : Task.FromResult(_planningResults.Dequeue());
    }

    public Task<ReviewUnitResult> ReviewUnitAsync(ReviewExecutionUnit unit, CancellationToken cancellationToken = default)
    {
        UnitReviewCount++;
        return Task.FromResult(_unitResults.Dequeue());
    }
}

public sealed class PrimaryModelFailoverReviewerTests
{
    private static readonly ChangedFile File = new("src/Foo.cs", ChangeKind.Modified, [new LineRange(1, 2)]);

    [Fact]
    public void ConstructorRejectsNullSession()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => new PrimaryModelFailoverReviewer([null!]));

        Assert.Contains("must not contain null", exception.Message);
    }

    [Fact]
    public async Task HealthyPrimaryNeverTouchesFallback()
    {
        var primary = Session("primary", false, Success("primary findings"));
        var fallback = Session("backup", true, Success("backup findings"));
        var reviewer = new PrimaryModelFailoverReviewer([primary, fallback]);

        await reviewer.InitializeAsync();
        FileReviewResult result = await reviewer.ReviewAsync(File);

        Assert.Equal("primary findings", result.Findings);
        Assert.Equal("primary-model", result.ReviewModelName);
        Assert.Equal("primary", result.ReviewModelProfile);
        Assert.Equal(1, primary.VerificationCount);
        Assert.Equal(1, primary.ReviewCount);
        Assert.Equal(0, fallback.VerificationCount);
        Assert.Equal(0, fallback.ReviewCount);
        Assert.Empty(reviewer.Failures);
    }

    [Fact]
    public async Task FailedPrimaryVerificationSelectsFallback()
    {
        var primary = Session("primary", false, Success("unused"), verification: new VerificationResult(false, "endpoint unavailable"));
        var fallback = Session("backup", true, Success("backup findings"));
        var observedTargets = new List<string>();
        var reviewer = new PrimaryModelFailoverReviewer([primary, fallback], target => observedTargets.Add(target.Name));

        await reviewer.InitializeAsync();
        FileReviewResult result = await reviewer.ReviewAsync(File);

        Assert.Equal("backup", reviewer.ActiveTarget!.Name);
        Assert.Equal("backup-model", result.ReviewModelName);
        Assert.Equal(0, primary.ReviewCount);
        Assert.Equal(1, fallback.VerificationCount);
        Assert.Single(reviewer.Failures);
        Assert.Contains("endpoint unavailable", reviewer.Failures[0].Reason);
        Assert.Equal(["primary", "backup"], observedTargets);
    }

    [Fact]
    public async Task ModelFailureRestartsWholeFileOnFallbackAndKeepsOneResult()
    {
        var partial = new FileReviewResult(File, FileReviewStatus.Partial, "discarded primary part", 1, 2, "part 2 failed", FailureKind: FileReviewFailureKind.Model);
        var primary = Session("primary", false, partial);
        var fallback = Session("backup", true, Success("complete backup findings"));
        var reviewer = new PrimaryModelFailoverReviewer([primary, fallback]);

        await reviewer.InitializeAsync();
        FileReviewResult result = await reviewer.ReviewAsync(File);

        Assert.Equal(FileReviewStatus.Reviewed, result.Status);
        Assert.Equal("complete backup findings", result.Findings);
        Assert.DoesNotContain("discarded", result.Findings);
        Assert.Equal("backup", result.ReviewModelProfile);
        Assert.Equal(1, primary.ReviewCount);
        Assert.Equal(1, fallback.ReviewCount);
        Assert.Single(reviewer.Failures);
    }

    [Fact]
    public async Task RepositoryFailureDoesNotActivateFallback()
    {
        var repositoryFailure = new FileReviewResult(File, FileReviewStatus.Failed, null, 0, 0, "file disappeared", FailureKind: FileReviewFailureKind.Repository);
        var primary = Session("primary", false, repositoryFailure);
        var fallback = Session("backup", true, Success("unused"));
        var reviewer = new PrimaryModelFailoverReviewer([primary, fallback]);

        await reviewer.InitializeAsync();
        FileReviewResult result = await reviewer.ReviewAsync(File);

        Assert.Equal(FileReviewFailureKind.Repository, result.FailureKind);
        Assert.Null(result.ReviewModelName);
        Assert.Equal(0, fallback.VerificationCount);
        Assert.Empty(reviewer.Failures);
    }

    [Fact]
    public async Task CancellationStopsWithoutActivatingFallback()
    {
        var primary = Session("primary", false, Success("unused"));
        primary.CancelReview = true;
        var fallback = Session("backup", true, Success("unused"));
        var reviewer = new PrimaryModelFailoverReviewer([primary, fallback]);

        await reviewer.InitializeAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => reviewer.ReviewAsync(File));
        Assert.Equal(0, fallback.VerificationCount);
    }

    [Fact]
    public async Task PlanningBeforeInitializationFailsClearly()
    {
        var reviewer = new PrimaryModelFailoverReviewer([Session("primary", false, Success("unused"))]);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => reviewer.PlanAsync("system", "user"));

        Assert.Contains("InitializeAsync", exception.Message);
    }

    [Fact]
    public async Task InitializationFailsAfterEveryTargetFailsVerification()
    {
        var primary = Session("primary", false, Success("unused"), verification: new VerificationResult(false, "primary down"));
        var fallback = Session("backup", true, Success("unused"), verification: new VerificationResult(false, "backup down"));
        var reviewer = new PrimaryModelFailoverReviewer([primary, fallback]);

        InformantFatalException exception = await Assert.ThrowsAsync<InformantFatalException>(() => reviewer.InitializeAsync());

        Assert.Contains("primary down", exception.Message);
        Assert.Contains("backup down", exception.Message);
        Assert.Equal(2, reviewer.Failures.Count);
    }

    /// <summary>Verifies a planning transport failure retries the complete batch on the next verified model</summary>
    [Fact]
    public async Task PlanningFailureRestartsBatchOnFallback()
    {
        var primary = Session("primary", false, Success("unused"));
        primary.FailPlanning = true;
        var fallback = Session("backup", true, Success("unused"));
        fallback.EnqueuePlanningResult("fallback plan");
        var reviewer = new PrimaryModelFailoverReviewer([primary, fallback]);
        await reviewer.InitializeAsync();

        string response = await reviewer.PlanAsync("system", "user");

        Assert.Equal("fallback plan", response);
        Assert.Equal(1, primary.PlanningCount);
        Assert.Equal(1, fallback.PlanningCount);
        Assert.Equal("backup", reviewer.ActiveTarget!.Name);
    }

    /// <summary>Verifies a clustered model failure discards the failed attempt and restarts the complete unit on the fallback</summary>
    [Fact]
    public async Task ClusteredFailureRestartsWholeUnitOnFallback()
    {
        ReviewExecutionUnit unit = Unit();
        var primary = Session("primary", false, Success("unused"));
        primary.EnqueueUnitResult(new ReviewUnitResult(unit, [], FileReviewFailureKind.Model, "primary failed"));
        var fallback = Session("backup", true, Success("unused"));
        fallback.EnqueueUnitResult(new ReviewUnitResult(unit, [new ReviewUnitPartResult(unit.Parts[0], "backup findings", Severity.Low, true)], FileReviewFailureKind.None, null));
        var reviewer = new PrimaryModelFailoverReviewer([primary, fallback]);
        await reviewer.InitializeAsync();

        ReviewUnitResult result = await reviewer.ReviewUnitAsync(unit);

        Assert.True(result.Succeeded);
        Assert.Equal("backup findings", Assert.Single(result.PartResults).Findings);
        Assert.Equal("backup-model", result.ReviewModelName);
        Assert.Equal(1, primary.UnitReviewCount);
        Assert.Equal(1, fallback.UnitReviewCount);
    }

    private static StubPrimaryModelSession Session(string name, bool fallback, FileReviewResult result, VerificationResult? verification = null) =>
        new(new PrimaryModelTarget(name, $"http://{name}.example/v1", $"{name}-model", fallback), verification ?? new VerificationResult(true, "verified"), result);

    private static FileReviewResult Success(string findings) => new(File, FileReviewStatus.Reviewed, findings, 1, 1, null, CandidateSeverityDetermined: true);

    private static ReviewExecutionUnit Unit()
    {
        var part = new ReviewUnitPart("part-000001", File, 1, 1, 1, 2, "source", 6);
        return new ReviewExecutionUnit("unit", "rationale", [], [part], "prompt");
    }
}
