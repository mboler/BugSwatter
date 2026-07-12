namespace BugSwatter.Email.Tests;

// Opt-in live integration test. It sends one real message and is skipped unless every required environment
// variable is present, so ordinary local and CI test runs remain offline. To run it, set:
//   BUGSWATTER_EMAIL_IT=1
//   BUGSWATTER_EMAIL_IT_ACS_CONNECTION=<ACS connection string>
//   BUGSWATTER_EMAIL_IT_FROM=<verified ACS sender address>
//   BUGSWATTER_EMAIL_IT_TO=<test recipient address>
/// <summary>Exercises the ACS transport against an explicitly configured live email resource</summary>
public sealed class AcsEmailLiveIntegrationTests
{
    /// <summary>Sends one message and verifies that ACS completes the send operation successfully</summary>
    [Fact]
    public async Task SendsMessageThroughConfiguredAcsResource()
    {
        Assert.SkipUnless(AcsEmailLiveConfiguration.Enabled, AcsEmailLiveConfiguration.DisabledReason);

        DateTimeOffset sentAt = DateTimeOffset.UtcNow;
        var message = new EmailMessage(
            AcsEmailLiveConfiguration.From,
            [AcsEmailLiveConfiguration.To],
            $"BugSwatter ACS integration test {sentAt:yyyy-MM-dd HH:mm:ss} UTC",
            $"This is an opt-in live integration test from BugSwatter.{Environment.NewLine}{Environment.NewLine}Sent at {sentAt:O}.{Environment.NewLine}No action is required.");
        var sender = new AcsEmailSender(AcsEmailLiveConfiguration.ConnectionString);
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        EmailSendReceipt receipt = await sender.SendAsync(message, timeoutSource.Token);

        Assert.Equal("AcceptedForDelivery", receipt.Decision);
        Assert.False(string.IsNullOrWhiteSpace(receipt.MessageId));
        Assert.Contains("Succeeded", receipt.Detail, StringComparison.OrdinalIgnoreCase);
    }
}

file static class AcsEmailLiveConfiguration
{
    private const string MasterSwitchName = "BUGSWATTER_EMAIL_IT";
    private const string ConnectionStringName = "BUGSWATTER_EMAIL_IT_ACS_CONNECTION";
    private const string FromName = "BUGSWATTER_EMAIL_IT_FROM";
    private const string ToName = "BUGSWATTER_EMAIL_IT_TO";

    private static readonly bool _switchedOn = ReadSwitch();

    internal static string ConnectionString { get; } = Environment.GetEnvironmentVariable(ConnectionStringName) ?? "";

    internal static string From { get; } = Environment.GetEnvironmentVariable(FromName) ?? "";

    internal static string To { get; } = Environment.GetEnvironmentVariable(ToName) ?? "";

    internal static bool Enabled => _switchedOn && ConnectionString.Length > 0 && From.Length > 0 && To.Length > 0;

    internal static string DisabledReason => $"live ACS email test is opt-in: set {MasterSwitchName}=1, {ConnectionStringName}, {FromName}, and {ToName}";

    private static bool ReadSwitch()
    {
        string value = Environment.GetEnvironmentVariable(MasterSwitchName) ?? "";
        return value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
