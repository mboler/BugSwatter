namespace BugSwatter.Email;

/// <summary>Connection and authentication options for an SMTP relay</summary>
public sealed record SmtpEmailOptions
{
    /// <summary>Creates validated SMTP transport options</summary>
    public SmtpEmailOptions(string host, int port, bool useStartTls, string? username = null, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "SMTP port must be between 1 and 65535");
        }

        if (!string.IsNullOrWhiteSpace(username) && string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("An SMTP password is required when a username is configured", nameof(password));
        }

        Host = host;
        Port = port;
        UseStartTls = useStartTls;
        Username = username;
        Password = password;
    }

    /// <summary>SMTP relay hostname</summary>
    public string Host { get; }

    /// <summary>SMTP relay port</summary>
    public int Port { get; }

    /// <summary>Whether to negotiate STARTTLS after connecting</summary>
    public bool UseStartTls { get; }

    /// <summary>Optional SMTP username</summary>
    public string? Username { get; }

    /// <summary>Resolved SMTP password, required with a username</summary>
    public string? Password { get; }

    /// <summary>True when relay authentication is configured</summary>
    public bool RequiresAuthentication => !string.IsNullOrWhiteSpace(Username);
}
