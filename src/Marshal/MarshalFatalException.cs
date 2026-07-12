namespace Marshal;

/// <summary>Fatal, user-actionable error that aborts Marshal with a clean message instead of a stack trace</summary>
public sealed class MarshalFatalException : Exception
{
    /// <summary>Creates the exception with the message shown to the user</summary>
    public MarshalFatalException(string message) : base(message)
    { }

    /// <summary>Creates the exception with the message shown to the user and the underlying cause</summary>
    public MarshalFatalException(string message, Exception innerException) : base(message, innerException)
    { }
}
