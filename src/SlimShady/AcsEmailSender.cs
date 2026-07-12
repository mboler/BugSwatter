using Azure;
using Azure.Communication.Email;

namespace SlimShady;

/// <summary>Sends report email through Azure Communication Services Email. The sender address must belong to a domain verified in the ACS resource, and the connection string comes from the environment via the config's env reference</summary>
public sealed class AcsEmailSender : IEmailSender
{
    private readonly EmailConfig _config;
    private readonly string _connectionString;

    /// <summary>Creates a sender for the given email config and resolved ACS connection string</summary>
    public AcsEmailSender(EmailConfig config, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        
        _config = config;
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task SendAsync(EmailReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var client = new EmailClient(_connectionString);
        var recipients = new EmailRecipients(_config.To.Select(address => new EmailAddress(address)).ToList());
        var content = new EmailContent(report.Subject) { PlainText = report.Body };
        var message = new EmailMessage(_config.From, recipients, content);

        foreach (string path in report.AttachmentPaths)
        {
            if (File.Exists(path))
            {
                byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                message.Attachments.Add(new EmailAttachment(Path.GetFileName(path), "text/markdown", new BinaryData(bytes)));
            }
        }

        // WaitUntil.Started returns as soon as ACS accepts the message; delivery is asynchronous on the service side
        await client.SendAsync(WaitUntil.Started, message, cancellationToken);
    }
}
