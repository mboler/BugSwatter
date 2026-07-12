using Azure;
using Azure.Communication.Email;
using AcsMessage = Azure.Communication.Email.EmailMessage;

namespace BugSwatter.Email;

/// <summary>Sends email through Azure Communication Services Email</summary>
public sealed class AcsEmailSender : IEmailSender
{
    private readonly string _connectionString;

    /// <summary>Creates a sender for a resolved ACS connection string</summary>
    public AcsEmailSender(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<EmailSendReceipt> SendAsync(EmailMessage email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        var client = new EmailClient(_connectionString);
        var recipients = new EmailRecipients(email.Recipients.Select(address => new EmailAddress(address)).ToList());
        var content = new EmailContent(email.Subject) { PlainText = email.Body };
        var message = new AcsMessage(email.From, recipients, content);

        foreach (EmailFileAttachment file in email.Attachments)
        {
            if (File.Exists(file.Path))
            {
                byte[] bytes = await File.ReadAllBytesAsync(file.Path, cancellationToken);
                message.Attachments.Add(new EmailAttachment(Path.GetFileName(file.Path), file.ContentType, new BinaryData(bytes)));
            }
        }

        // Completed confirms the ACS send operation succeeded, but it does not prove recipient delivery
        EmailSendOperation operation = await client.SendAsync(WaitUntil.Completed, message, cancellationToken);
        return new EmailSendReceipt("AcceptedForDelivery", operation.Id, $"ACS operation status: {operation.Value.Status}");
    }
}
