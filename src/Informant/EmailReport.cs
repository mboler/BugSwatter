using BugSwatter.Email;

namespace Informant;

/// <summary>The outcome of the report-email step, appended to the validated report so the run's own artifact records what was sent, to whom and when</summary>
public sealed record EmailDeliveryRecord(string Decision, DateTimeOffset Time, string Provider, IReadOnlyList<string> Recipients, string Detail)
{
    /// <summary>Renders the record as a Markdown section for the validated report</summary>
    public string ToMarkdownSection()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("## Email delivery");
        builder.AppendLine();
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine($"| Decision | {Decision} |");
        builder.AppendLine($"| Time | {Time:yyyy-MM-dd HH:mm:ss zzz} |");
        builder.AppendLine($"| Provider | {Provider} |");
        builder.AppendLine($"| Recipients | {(Recipients.Count == 0 ? "(none)" : string.Join(", ", Recipients))} |");
        builder.AppendLine($"| Detail | {Detail} |");
        builder.AppendLine();

        return builder.ToString();
    }
}

/// <summary>Builds the report email from a completed run: a summary body plus the two Markdown reports as attachments</summary>
public static class EmailReportBuilder
{
    /// <summary>Assembles the email for a run whose Second Opinion completed</summary>
    /// <param name="from">Configured sender address</param>
    /// <param name="recipients">Configured recipient addresses</param>
    /// <param name="repositoryUrl">Repository under review, named in the subject</param>
    /// <param name="branch">Branch under review</param>
    /// <param name="localReportPath">The raw local-review report, attached</param>
    /// <param name="outcome">The completed second-opinion result driving the subject severity and attachments</param>
    /// <param name="severityUndetermined">True when the JSON did not parse, so the body flags that severity could not be read</param>
    /// <param name="attachReports">Whether to attach the Markdown reports</param>
    public static EmailMessage Build(string from, IReadOnlyList<string> recipients, string repositoryUrl, string branch, string localReportPath, SecondOpinionOutcome outcome, bool severityUndetermined,
        bool attachReports)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        string severityLabel = severityUndetermined ? "undetermined" : outcome.MaxSeverity.ToString();
        string subject = $"Informant review: {repositoryUrl} ({branch}) - max severity {severityLabel}";

        var body = new System.Text.StringBuilder();
        body.AppendLine($"Informant reviewed {repositoryUrl} on branch {branch}.");
        body.AppendLine();
        body.AppendLine($"Files validated by the second opinion: {outcome.ValidatedCount}");
        body.AppendLine($"Files whose validation failed: {outcome.FailedCount}");
        body.AppendLine($"Highest confirmed severity: {severityLabel}");
        if (severityUndetermined)
        {
            body.AppendLine();
            body.Append("Note: the second-opinion model did not return parseable structured findings, so severity could not be determined. ");
            body.AppendLine("This notification is attempted anyway; read the attached validated report for the details.");
        }

        body.AppendLine();
        body.AppendLine("The raw local review and the validated second-opinion review are attached. The validated report is the one to read first: it confirms the real findings and discards the noise.");

        List<EmailFileAttachment> attachments = attachReports ? [new(localReportPath, "text/markdown"), new(outcome.ValidatedReportPath, "text/markdown")] : [];
        return new EmailMessage(from, recipients, subject, body.ToString(), attachments);
    }
}
