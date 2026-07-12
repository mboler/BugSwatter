namespace Informant;

/// <summary>Fatal, user-actionable error that aborts the run with a clean message instead of a stack trace</summary>
public sealed class InformantFatalException : Exception
{
    /// <summary>Creates the exception with the message shown to the user</summary>
    public InformantFatalException(string message) : base(message)
    { }

    /// <summary>Creates the exception with the message shown to the user and the underlying cause</summary>
    public InformantFatalException(string message, Exception innerException) : base(message, innerException)
    { }
}
