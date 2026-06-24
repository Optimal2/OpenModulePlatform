namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed record ScCommandResult(int ExitCode, string Output, string Error)
{
    private const int ServiceNotFoundExitCode = 1060;
    private const int ServiceCannotAcceptControlExitCode = 1061;
    private const int ServiceMarkedForDeletionExitCode = 1072;

    public string CombinedOutput => string.Concat(Output, "\n", Error);

    public bool IsServiceNotFound()
        => ExitCode == ServiceNotFoundExitCode
            || CombinedOutput.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase)
            || CombinedOutput.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    public bool IsServiceControlTemporarilyUnavailable()
        => ExitCode == ServiceCannotAcceptControlExitCode
            || CombinedOutput.Contains("FAILED 1061", StringComparison.OrdinalIgnoreCase)
            || CombinedOutput.Contains("cannot accept control messages", StringComparison.OrdinalIgnoreCase);

    public bool IsServiceMarkedForDeletion()
        => ExitCode == ServiceMarkedForDeletionExitCode
            || CombinedOutput.Contains("FAILED 1072", StringComparison.OrdinalIgnoreCase)
            || CombinedOutput.Contains("marked for deletion", StringComparison.OrdinalIgnoreCase);
}
