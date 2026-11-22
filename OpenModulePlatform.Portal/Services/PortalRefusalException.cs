namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// A prepared refusal: the Portal declined an operator's request for a reason the operator
/// can act on, and <see cref="Exception.Message"/> is written to be shown to them.
/// </summary>
/// <remarks>
/// Page handlers used to recognise these by catching InvalidOperationException, which is
/// far wider than the refusals it was meant for. SqlClient reports an exhausted connection
/// pool as an InvalidOperationException too, and so do many programming errors; caught that
/// way they were shown to the operator as raw text in the status line and never reached the
/// log. A handler that wants to show a refusal catches this type instead, and everything
/// else goes to the ordinary error handling, which logs it.
///
/// It derives from InvalidOperationException so that existing callers and tests that expect
/// that type from a service keep working; only the handlers that display the message need
/// to tell the two apart.
/// </remarks>
public sealed class PortalRefusalException : InvalidOperationException
{
    public PortalRefusalException(string message)
        : base(message)
    {
    }

    public PortalRefusalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
