namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Recursive directory deletion with retries, shared by the job processor and self-upgrade.
/// Antivirus scanners and lingering service handles keep files locked for a short while
/// after a service stops, so the first attempts are expected to fail.
/// </summary>
internal static class DirectoryDeletion
{
    private const int MaxAttempts = 20;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    public static void DeleteWithRetry(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                WaitBeforeRetry(cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < MaxAttempts)
            {
                WaitBeforeRetry(cancellationToken);
            }
        }
    }

    private static void WaitBeforeRetry(CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(RetryDelay))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
