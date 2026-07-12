namespace BugSwatter.Email;

/// <summary>One file attached to an email, with its explicit MIME content type</summary>
public sealed record EmailFileAttachment
{
    /// <summary>Creates a file attachment</summary>
    public EmailFileAttachment(string path, string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        Path = path;
        ContentType = contentType;
    }

    /// <summary>Path of the file to attach</summary>
    public string Path { get; }

    /// <summary>MIME content type sent with the attachment</summary>
    public string ContentType { get; }
}

/// <summary>A transport-neutral email with sender, recipients, text content, and optional file attachments</summary>
public sealed record EmailMessage
{
    /// <summary>Creates an email and snapshots its recipient and attachment collections</summary>
    public EmailMessage(string from, IReadOnlyList<string> recipients, string subject, string body, IReadOnlyList<EmailFileAttachment>? attachments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(body);
        if (recipients.Count == 0 || recipients.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty recipient is required", nameof(recipients));
        }

        From = from;
        Recipients = [.. recipients];
        Subject = subject;
        Body = body;
        Attachments = attachments is null ? [] : [.. attachments];
    }

    /// <summary>Sender address</summary>
    public string From { get; }

    /// <summary>Recipient addresses</summary>
    public IReadOnlyList<string> Recipients { get; }

    /// <summary>Message subject</summary>
    public string Subject { get; }

    /// <summary>Plain-text message body</summary>
    public string Body { get; }

    /// <summary>Files attached to the message</summary>
    public IReadOnlyList<EmailFileAttachment> Attachments { get; }
}

/// <summary>What an email transport confirmed after accepting a message</summary>
public sealed record EmailSendReceipt(string Decision, string? MessageId, string Detail);

/// <summary>Sends a transport-neutral email</summary>
public interface IEmailSender
{
    /// <summary>Sends the email and returns the transport's acceptance receipt</summary>
    Task<EmailSendReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
