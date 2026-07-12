using System.Net;
using System.Net.Mail;

namespace Informant;

/// <summary>Sends report email over SMTP using the framework's built-in client, so the tool carries no third-party email or crypto dependency. Supports STARTTLS (typically port 587) and unencrypted (port 25); implicit TLS on 465 is not supported by the built-in client, use 587 with STARTTLS instead. Attachments are sent as text/markdown; the password is resolved from the environment or a secret file via the config reference</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailConfig _config;
    private readonly string? _password;

    /// <summary>Creates a sender for the given email config; the resolved password may be null when the relay needs no authentication</summary>
    public SmtpEmailSender(EmailConfig config, string? password)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _password = password;
    }

    /// <inheritdoc />
    public async Task<EmailSendReceipt> SendAsync(EmailReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var message = new MailMessage { From = new MailAddress(_config.From), Subject = report.Subject, Body = report.Body };
        foreach (string recipient in _config.To)
        {
            message.To.Add(recipient);
        }

        // Attachments own file handles, so they are tracked and disposed explicitly once the message is sent
        var attachments = new List<Attachment>();
        try
        {
            foreach (string path in report.AttachmentPaths)
            {
                if (File.Exists(path))
                {
                    var attachment = new Attachment(path, "text/markdown");
                    attachments.Add(attachment);
                    message.Attachments.Add(attachment);
                }
            }

            // EnableSsl on the built-in client means STARTTLS: TLS is negotiated after connecting on the plain port
            using var client = new SmtpClient(_config.SmtpHost, _config.SmtpPort) { EnableSsl = _config.UseStartTls, DeliveryMethod = SmtpDeliveryMethod.Network };
            if (_config.RequiresAuthentication)
            {
                client.Credentials = new NetworkCredential(_config.Username, _password);
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
