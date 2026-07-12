using System.Net;
using System.Net.Mail;

namespace BugSwatter.Email;

/// <summary>Sends email over SMTP using the framework's built-in client. Supports STARTTLS and unencrypted relays;
/// implicit TLS on port 465 is not supported by the built-in client</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpEmailOptions _options;

    /// <summary>Creates a sender for the validated SMTP transport options</summary>
    public SmtpEmailSender(SmtpEmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public async Task<EmailSendReceipt> SendAsync(EmailMessage email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        using var message = new MailMessage { From = new MailAddress(email.From), Subject = email.Subject, Body = email.Body };
        foreach (string recipient in email.Recipients)
        {
            message.To.Add(recipient);
        }

        // Attachments own file handles, so they are tracked and disposed explicitly once the message is sent
        var attachments = new List<Attachment>();
        try
        {
            foreach (EmailFileAttachment file in email.Attachments)
            {
                if (File.Exists(file.Path))
                {
                    var attachment = new Attachment(file.Path, file.ContentType);
                    attachments.Add(attachment);
                    message.Attachments.Add(attachment);
                }
            }

            // EnableSsl on the built-in client means STARTTLS: TLS is negotiated after connecting on the plain port
            using var client = new SmtpClient(_options.Host, _options.Port) { EnableSsl = _options.UseStartTls, DeliveryMethod = SmtpDeliveryMethod.Network };
            if (_options.RequiresAuthentication)
            {
                client.Credentials = new NetworkCredential(_options.Username, _options.Password);
            }

            await client.SendMailAsync(message, cancellationToken);
            return new EmailSendReceipt("AcceptedForDelivery", null, "the SMTP relay accepted the message");
        }
        finally
        {
            foreach (Attachment attachment in attachments)
            {
                attachment.Dispose();
            }
        }
    }
}
