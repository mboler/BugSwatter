namespace Marshal.Tests;

/// <summary>Regression tests for bounded webhook retry suppression</summary>
public sealed class WebhookDeliveryTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Verifies that one provider delivery is claimed only once during the retention window</summary>
    [Fact]
    public void DuplicateDeliveryIsRejectedDuringRetentionWindow()
    {
        var tracker = new WebhookDeliveryTracker();

        Assert.True(tracker.TryClaim(WebhookProvider.GitHub, "delivery-1", Start));
        Assert.False(tracker.TryClaim(WebhookProvider.GitHub, "delivery-1", Start.AddHours(1)));
    }

    /// <summary>Verifies that an old delivery ID expires and can be accepted again</summary>
    [Fact]
    public void DeliveryExpiresAfterRetentionWindow()
    {
        var tracker = new WebhookDeliveryTracker();
        Assert.True(tracker.TryClaim(WebhookProvider.AzureDevOps, "delivery-1", Start));

        Assert.True(tracker.TryClaim(WebhookProvider.AzureDevOps, "delivery-1", Start.AddHours(WebhookDeliveryTracker.RetentionHours)));
    }

    /// <summary>Verifies that provider namespaces prevent unrelated equal IDs from colliding</summary>
    [Fact]
    public void ProvidersHaveIndependentDeliveryNamespaces()
    {
        var tracker = new WebhookDeliveryTracker();

        Assert.True(tracker.TryClaim(WebhookProvider.GitHub, "shared-id", Start));
        Assert.True(tracker.TryClaim(WebhookProvider.AzureDevOps, "shared-id", Start));
    }

    /// <summary>Verifies that a failed enqueue can release its delivery claim for a provider retry</summary>
    [Fact]
    public void ForgottenDeliveryCanBeClaimedAgain()
    {
        var tracker = new WebhookDeliveryTracker();
        Assert.True(tracker.TryClaim(WebhookProvider.GitHub, "delivery-1", Start));

        tracker.Forget(WebhookProvider.GitHub, "delivery-1");

        Assert.True(tracker.TryClaim(WebhookProvider.GitHub, "delivery-1", Start));
    }

    /// <summary>Verifies that the oldest delivery is evicted when the fixed memory bound is reached</summary>
    [Fact]
    public void OldestDeliveryIsEvictedAtCapacity()
    {
        var tracker = new WebhookDeliveryTracker();
        for (int number = 0; number < WebhookDeliveryTracker.MaxTrackedDeliveries; number++)
        {
            Assert.True(tracker.TryClaim(WebhookProvider.GitHub, $"delivery-{number}", Start.AddSeconds(number)));
        }

        Assert.True(tracker.TryClaim(WebhookProvider.GitHub, "overflow", Start.AddHours(2)));
        Assert.True(tracker.TryClaim(WebhookProvider.GitHub, "delivery-0", Start.AddHours(2)));
    }
}
