using BugSwatter.Common;

namespace Marshal.Tests;

public sealed class CurrentReviewStatusStoreTests
{
    [Fact]
    public void ProgressUpdatesTheMatchingRunAndClearReturnsToIdle()
    {
        var store = new CurrentReviewStatusStore();
        var job = new ReviewJobConfig { Name = "nightly", InformantConfigPath = @"C:\jobs\nightly\informant.json" };
        var request = new ReviewRequest(job, "schedule");
        DateTimeOffset startedUtc = DateTimeOffset.Parse("2026-07-13T07:00:00Z");

        store.Begin(request, startedUtc);
        store.Apply(job.Name, new ReviewProgressSnapshot
        {
            Phase = "Primary review",
            ModelName = "local-model",
            ModelProfile = "primary",
            CurrentFile = "src/Worker.cs",
            FileIndex = 4,
            FileCount = 10,
            ModelRequestActive = true,
            ModelRequestStartedUtc = startedUtc.AddMinutes(1),
            ModelRequestCount = 8,
            TotalTokens = 4200
        });

        CurrentReviewActivity activity = Assert.IsType<CurrentReviewActivity>(store.Snapshot());
        Assert.Equal("nightly", activity.Job);
        Assert.Equal("schedule", activity.Trigger);
        Assert.Equal(startedUtc, activity.StartedUtc);
        Assert.Equal("Primary review", activity.Phase);
        Assert.Equal("local-model", activity.ModelName);
        Assert.Equal("src/Worker.cs", activity.CurrentFile);
        Assert.True(activity.ModelRequestActive);
        Assert.Equal(startedUtc.AddMinutes(1), activity.ModelRequestStartedUtc);
        Assert.Equal(4200, activity.TotalTokens);

        store.Clear(job.Name);
        Assert.Null(store.Snapshot());
    }

    [Fact]
    public void StaleProgressAndClearForAnotherJobAreIgnored()
    {
        var store = new CurrentReviewStatusStore();
        var job = new ReviewJobConfig { Name = "current", InformantConfigPath = @"C:\jobs\current\informant.json" };
        store.Begin(new ReviewRequest(job, "manual"), DateTimeOffset.UtcNow);

        store.Apply("old-job", new ReviewProgressSnapshot { Phase = "Completed" });
        store.Clear("old-job");

        CurrentReviewActivity activity = Assert.IsType<CurrentReviewActivity>(store.Snapshot());
        Assert.Equal("Starting Informant", activity.Phase);
    }
}
