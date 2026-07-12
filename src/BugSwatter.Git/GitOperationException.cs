namespace BugSwatter.Git;

/// <summary>User-actionable failure while launching Git or safely managing a working tree</summary>
public sealed class GitOperationException : Exception
{
    /// <summary>Creates an exception with a user-actionable message</summary>
    public GitOperationException(string message) : base(message)
    { }

    /// <summary>Creates an exception with a user-actionable message and underlying cause</summary>
    public GitOperationException(string message, Exception innerException) : base(message, innerException)
    { }
}
