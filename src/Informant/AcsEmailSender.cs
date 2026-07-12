using Azure;
using Azure.Communication.Email;

namespace Informant;

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
    public async Task<EmailSendReceipt> SendAsync(EmailReport report, CancellationToken cancellationToken = default)
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

        // Completed confirms the ACS send operation succeeded, but it does not prove recipient delivery
        EmailSendOperation operation = await client.SendAsync(WaitUntil.Completed, message, cancellationToken);
        return new EmailSendReceipt("AcceptedForDelivery", operation.Id, $"ACS operation status: {operation.Value.Status}");
    }
}
