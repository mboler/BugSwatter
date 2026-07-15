using System.Text.Json;

namespace Informant.Tests;

public sealed class EmailTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Theory]
    [InlineData(EmailSendOn.Always, Severity.None, true)]
    [InlineData(EmailSendOn.Always, Severity.Critical, true)]
    [InlineData(EmailSendOn.Medium, Severity.Low, false)]
    [InlineData(EmailSendOn.Medium, Severity.Medium, true)]
    [InlineData(EmailSendOn.Medium, Severity.High, true)]
    [InlineData(EmailSendOn.High, Severity.Medium, false)]
    [InlineData(EmailSendOn.High, Severity.High, true)]
    [InlineData(EmailSendOn.High, Severity.Critical, true)]
    public void ShouldSendHonorsTheSeverityThreshold(EmailSendOn sendOn, Severity maxSeverity, bool expected)
    {
        var config = new EmailConfig { SendOn = sendOn };
        Assert.Equal(expected, config.ShouldSend(maxSeverity));
    }

    [Theory]
    [InlineData(EmailSendOn.Medium)]
    [InlineData(EmailSendOn.High)]
    public void ShouldSendFailsOpenWhenSeverityIsUndetermined(EmailSendOn sendOn)
    {
        var config = new EmailConfig { SendOn = sendOn };
        Assert.True(config.ShouldSend(Severity.None, severityDetermined: false));
    }

    [Fact]
    public void ValidBlockLoads()
    {
        WriteConfigWithEmail(new Dictionary<string, object?> { ["smtpHost"] = "smtp", ["from"] = "a@b.com", ["to"] = new[] { "c@d.com" }, ["sendOn"] = "high" });
        EmailConfig email = InformantConfig.Load(_directory.Path).Email!;
        Assert.Equal(EmailSendOn.High, email.SendOn);
        Assert.Equal("smtp", email.SmtpHost);
    }

    [Fact]
    public void MissingRequiredFieldsAreFatalAtLoad()
    {
        WriteConfigWithEmail(new Dictionary<string, object?> { ["smtpHost"] = "", ["from"] = "a@b.com", ["to"] = new[] { "c@d.com" } });
        Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));

        WriteConfigWithEmail(new Dictionary<string, object?> { ["smtpHost"] = "smtp", ["from"] = "a@b.com", ["to"] = Array.Empty<string>() });
        Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
    }

    [Fact]
    public void LiteralPasswordIsRejectedAtLoad()
    {
        WriteConfigWithEmail(new Dictionary<string, object?> { ["smtpHost"] = "smtp", ["from"] = "a@b.com", ["to"] = new[] { "c@d.com" }, ["username"] = "user", ["password"] = "literal-secret" });
        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains("env:VARIABLE_NAME", ex.Message);
    }

    [Fact]
    public void EmailWithoutSecondOpinionIsFatalAtLoad()
    {
        WriteConfigWithEmail(new Dictionary<string, object?> { ["smtpHost"] = "smtp", ["from"] = "a@b.com", ["to"] = new[] { "c@d.com" } }, includeSecondOpinion: false);
        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains("secondOpinion", ex.Message);
    }

    [Fact]
    public void AcsProviderLoadsWithConnectionStringReference()
    {
        WriteConfigWithEmail(new Dictionary<string, object?> { ["provider"] = "azureCommunicationServices", ["from"] = "DoNotReply@d.com", ["to"] = new[] { "c@d.com" }, ["acsConnectionString"] = "env:INFORMANT_ACS_TEST" });
        EmailConfig email = InformantConfig.Load(_directory.Path).Email!;
        Assert.Equal(EmailProvider.AzureCommunicationServices, email.Provider);
    }

    [Fact]
    public void AcsProviderRequiresAnEnvConnectionStringReference()
    {
        WriteConfigWithEmail(new Dictionary<string, object?> { ["provider"] = "azureCommunicationServices", ["from"] = "DoNotReply@d.com", ["to"] = new[] { "c@d.com" }, ["acsConnectionString"] = "endpoint=https://x;accesskey=literal" });
        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains("env:VARIABLE_NAME", ex.Message);
    }

    [Fact]
    public void AcsProviderNeedsNoSmtpHost()
    {
        WriteConfigWithEmail(new Dictionary<string, object?> { ["provider"] = "azureCommunicationServices", ["from"] = "DoNotReply@d.com", ["to"] = new[] { "c@d.com" }, ["acsConnectionString"] = "env:INFORMANT_ACS_TEST" });
        Assert.NotNull(InformantConfig.Load(_directory.Path).Email);
    }

    [Fact]
    public void AcsConnectionStringResolvesFromEnvironment()
    {
        var config = new EmailConfig { Provider = EmailProvider.AzureCommunicationServices, AcsConnectionString = "env:INFORMANT_ACS_RESOLVE_TEST" };

        string? originalAcsValue = Environment.GetEnvironmentVariable("INFORMANT_ACS_RESOLVE_TEST");
        Environment.SetEnvironmentVariable("INFORMANT_ACS_RESOLVE_TEST", "endpoint=https://x;accesskey=secret");
        try
        {
            Assert.Equal("endpoint=https://x;accesskey=secret", config.ResolveAcsConnectionString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_ACS_RESOLVE_TEST", originalAcsValue);
        }

        Assert.Null(config.ResolveAcsConnectionString());
    }

    [Fact]
    public void ResolvePasswordReadsTheEnvironment()
    {
        var config = new EmailConfig { Username = "user", Password = "env:INFORMANT_SMTP_TEST" };

        string? originalSmtpValue = Environment.GetEnvironmentVariable("INFORMANT_SMTP_TEST");
        Environment.SetEnvironmentVariable("INFORMANT_SMTP_TEST", "smtp-secret");
        try
        {
            Assert.Equal("smtp-secret", config.ResolvePassword());
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_SMTP_TEST", originalSmtpValue);
        }

        Assert.Null(config.ResolvePassword());
    }

    [Fact]
    public void NoAuthConfigNeedsNoPassword()
    {
        var config = new EmailConfig { SmtpHost = "smtp", From = "a@b.com", To = ["c@d.com"] };
        Assert.False(config.RequiresAuthentication);
        Assert.Null(config.ResolvePassword());
    }

    [Fact]
    public void BuildSubjectCarriesRepoBranchAndSeverity()
    {
        var outcome = new SecondOpinionOutcome("v.md", "v.json", Severity.High, true, 3, 0);
        EmailMessage message = EmailReportBuilder.Build("sender@example.test", ["recipient@example.test"], "https://example.test/repo.git", "main", "local.md", outcome,
            severityUndetermined: false, attachReports: true);

        Assert.Contains("https://example.test/repo.git", message.Subject);
        Assert.Contains("main", message.Subject);
        Assert.Contains("High", message.Subject);
        Assert.Equal(["local.md", "v.md"], message.Attachments.Select(attachment => attachment.Path));
        Assert.Contains("Files validated by the second opinion: 3", message.Body);
    }

    [Fact]
    public void BuildFlagsUndeterminedSeverityAndCanOmitAttachments()
    {
        var outcome = new SecondOpinionOutcome("v.md", "v.json", Severity.None, false, 2, 0);
        EmailMessage message = EmailReportBuilder.Build("sender@example.test", ["recipient@example.test"], "repo", "dev", "local.md", outcome, severityUndetermined: true, attachReports: false);

        Assert.Contains("undetermined", message.Subject);
        Assert.Contains("could not be determined", message.Body);
        Assert.Empty(message.Attachments);
    }

    [Fact]
    public void EmailDeliveryRecordRendersAMarkdownSection()
    {
        var record = new EmailDeliveryRecord("AcceptedForDelivery", new DateTimeOffset(2026, 7, 11, 18, 57, 0, TimeSpan.FromHours(-5)), "Smtp", ["a@b.com", "c@d.com"],
            "subject: review; operation/message ID: abc123");
        string section = record.ToMarkdownSection();

        Assert.Contains("## Email delivery", section);
        Assert.Contains("| Decision | AcceptedForDelivery |", section);
        Assert.Contains("| Provider | Smtp |", section);
        Assert.Contains("| Recipients | a@b.com, c@d.com |", section);
        Assert.Contains("operation/message ID: abc123", section);
    }

    [Fact]
    public void EmailDeliveryRecordShowsNoneWhenNoRecipients()
    {
        var record = new EmailDeliveryRecord("Skipped", DateTimeOffset.Now, "Smtp", [], "below the send threshold");
        Assert.Contains("| Recipients | (none) |", record.ToMarkdownSection());
    }

    private void WriteConfigWithEmail(Dictionary<string, object?> email, bool includeSecondOpinion = true)
    {
        var values = new Dictionary<string, object?>
        {
            ["repositoryUrl"] = "https://example.test/repo.git",
            ["branch"] = "main",
            ["workingTreePath"] = Path.Combine(_directory.Path, "tree"),
            ["gitExecutablePath"] = TestGit.ExecutablePath,
            ["modelEndpoint"] = "http://localhost:1234/v1",
            ["modelName"] = "test-model",
            ["email"] = email
        };

        if (includeSecondOpinion)
        {
            values["secondOpinion"] = new Dictionary<string, object?> { ["endpoint"] = "http://localhost:1234/v1", ["modelName"] = "validator" };
        }

        File.WriteAllText(Path.Combine(_directory.Path, InformantConfig.FileName), JsonSerializer.Serialize(values));
    }
}
