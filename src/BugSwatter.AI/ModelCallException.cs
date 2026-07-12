namespace BugSwatter.AI;

/// <summary>Recoverable model-layer failure such as an unreachable endpoint, timeout, bad status, or malformed reply</summary>
public sealed class ModelCallException : Exception
{
    /// <summary>Creates the exception with the failure description</summary>
    public ModelCallException(string message) : base(message)
    { }

    /// <summary>Creates the exception with the failure description and the underlying cause</summary>
    public ModelCallException(string message, Exception innerException) : base(message, innerException)
    { }
}
