namespace Informant;

/// <summary>Recoverable model-layer failure (endpoint unreachable, timeout, bad status, empty or malformed reply); callers retry per config and then skip the file, unlike a <see cref="InformantFatalException"/> which aborts the run</summary>
public sealed class ModelCallException : Exception
{
    /// <summary>Creates the exception with the failure description</summary>
    public ModelCallException(string message) : base(message)
    { }

    /// <summary>Creates the exception with the failure description and the underlying cause</summary>
    public ModelCallException(string message, Exception innerException) : base(message, innerException)
    { }
}
