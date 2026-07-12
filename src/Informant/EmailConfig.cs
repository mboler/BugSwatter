using BugSwatter.Common;

namespace Informant;

/// <summary>Which transport sends the report email</summary>
public enum EmailProvider
{
    /// <summary>Plain SMTP via the framework's built-in client, works with any STARTTLS or unencrypted relay including Microsoft 365 SMTP AUTH</summary>
    Smtp,

    /// <summary>Azure Communication Services Email, authenticated with a connection string</summary>
    AzureCommunicationServices
}

/// <summary>When to send the report email, gated on the second-opinion max confirmed severity</summary>
public enum EmailSendOn
{
    /// <summary>Send whenever a second opinion completed, regardless of findings</summary>
    Always,

    /// <summary>Send when at least one confirmed finding is medium or worse</summary>
    Medium,

    /// <summary>Send when at least one confirmed finding is high or critical</summary>
    High
}

/// <summary>Configuration for emailing run reports. Present only when email is wanted; email is additionally gated on a Second Opinion having completed, so it never fires for a raw local review. The SMTP password is an env:VARIABLE_NAME reference, never a literal</summary>
public sealed record EmailConfig
{
    private string _configDirectory = Directory.GetCurrentDirectory();

    /// <summary>Which transport to use</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<EmailProvider>))]
    public EmailProvider Provider { get; init; } = EmailProvider.Smtp;

    /// <summary>SMTP server host (SMTP provider)</summary>
    public string SmtpHost { get; init; } = "";

    /// <summary>SMTP server port; 587 for STARTTLS or 25 for plain. Implicit TLS on 465 is not supported by the built-in client, use 587 with STARTTLS</summary>
    public int SmtpPort { get; init; } = 587;

    /// <summary>Whether to negotiate STARTTLS after connecting; set false only for an unencrypted relay on port 25</summary>
    public bool UseStartTls { get; init; } = true;

    /// <summary>From address</summary>
    public string From { get; init; } = "";

    /// <summary>Recipient addresses; at least one is required</summary>
    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>SMTP username; null or empty means the relay needs no authentication</summary>
    public string? Username { get; init; }

    /// <summary>SMTP password as an env:VARIABLE_NAME or file:PATH reference; literals are rejected, null means no authentication</summary>
    public string? Password { get; init; }

    /// <summary>Azure Communication Services connection string as an env:VARIABLE_NAME or file:PATH reference; literals are rejected (ACS provider)</summary>
    public string? AcsConnectionString { get; init; }

    /// <summary>Severity threshold that must be met for an email to send</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<EmailSendOn>))]
    public EmailSendOn SendOn { get; init; } = EmailSendOn.High;

    /// <summary>Whether to attach the Markdown reports; when false only the summary body is sent</summary>
    public bool AttachReports { get; init; } = true;

    /// <summary>True when SMTP authentication is configured</summary>
    public bool RequiresAuthentication => !string.IsNullOrWhiteSpace(Username);

    /// <summary>Reads the SMTP password from its env: or file: reference</summary>
    /// <returns>The password value, or null when no reference is configured or the source is unset</returns>
    public string? ResolvePassword() => SecretReference.Resolve(Password, _configDirectory);

    /// <summary>Reads the ACS connection string from its env: or file: reference</summary>
    /// <returns>The connection string, or null when no reference is configured or the source is unset</returns>
    public string? ResolveAcsConnectionString() => SecretReference.Resolve(AcsConnectionString, _configDirectory);

    /// <summary>Returns whether the run's max confirmed severity meets the send threshold; a None severity with an unparseable second opinion is handled by the caller's fail-open policy, not here</summary>
    public bool ShouldSend(Severity maxSeverity)
    {
        return SendOn switch
        {
            EmailSendOn.Always => true,
            EmailSendOn.Medium => maxSeverity >= Severity.Medium,
            EmailSendOn.High => maxSeverity >= Severity.High,
            _ => false
        };
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(From))
        {
            throw new InformantFatalException("email.from is required when the email block is present");
        }

        if (To.Count == 0 || To.Any(string.IsNullOrWhiteSpace))
        {
            throw new InformantFatalException("email.to must list at least one non-empty recipient");
        }

        switch (Provider)
        {
            case EmailProvider.Smtp:
                ValidateSmtp();
                break;

            case EmailProvider.AzureCommunicationServices:
                ValidateAcs();
                break;

            default:
                throw new InformantFatalException($"email.provider '{Provider}' is not supported");
        }
    }

    internal void SetConfigDirectory(string configDirectory) => _configDirectory = configDirectory;

    private void ValidateSmtp()
    {
        if (string.IsNullOrWhiteSpace(SmtpHost))
        {
            throw new InformantFatalException("email.smtpHost is required for the smtp provider");
        }

        if (SmtpPort is < 1 or > 65535)
        {
            throw new InformantFatalException($"email.smtpPort must be between 1 and 65535, got {SmtpPort}");
        }

        if (!string.IsNullOrWhiteSpace(Password) && !SecretReference.IsReference(Password))
        {
            throw new InformantFatalException("email.password must be an env:VARIABLE_NAME or file:PATH reference; SMTP passwords are never stored in the config file");
        }
    }

    private void ValidateAcs()
    {
        if (!SecretReference.IsReference(AcsConnectionString))
        {
            throw new InformantFatalException("email.acsConnectionString is required for the azureCommunicationServices provider and must be an env:VARIABLE_NAME or file:PATH reference; connection strings are never stored in the config file");
        }
    }
}
