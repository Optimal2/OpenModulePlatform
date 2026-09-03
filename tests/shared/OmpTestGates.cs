namespace OpenModulePlatform.TestSupport;

/// <summary>
/// Machine-wide gates for test classes that must not run concurrently across test
/// hosts because they share state outside the process -- typically the local
/// development database rather than a per-process test database.
/// </summary>
/// <remarks>
/// <para>
/// xUnit collections serialise tests within ONE process only. Two test hosts (a
/// local CI run next to a pre-push hook, a reviewer's worktree next to the main
/// tree) still interleave, and a test that claims "the next pending row" from a
/// shared table then sees the other host's row. Measured 2026-09-03 on
/// <c>OmpHostArtifactRepositoryHostDeploymentLeaseTests</c>: Expected 2396, Actual
/// 2395 under two concurrent hosts.
/// </para>
/// <para>
/// The gate is an exclusively opened lock file, not a named mutex: a mutex is
/// owned by a thread, and xUnit constructs and disposes a test class on whichever
/// pool thread is free, so releasing from another thread would fail and leave the
/// gate held until the process exits. A file handle is released by any thread and
/// by the OS when the process dies.
/// </para>
/// </remarks>
public static class OmpTestGates
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Acquires the gate named <paramref name="name"/>; dispose the result to
    /// release it. Throws when another host holds it for longer than five minutes,
    /// which is a real hang, not something to wait out silently.
    /// </summary>
    public static IDisposable Acquire(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("name must be a plain file-name-safe token.", nameof(name));
        }

        var path = Path.Combine(Path.GetTempPath(), "OpenModulePlatform.Tests." + name + ".lock");
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
                return new Release(stream);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // Held by another test host (or another class in this host). Wait.
                Thread.Sleep(RetryDelay);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(RetryDelay);
            }
            catch (IOException ex)
            {
                throw new TimeoutException(
                    $"Test gate '{name}' was held by another test host for more than {WaitTimeout.TotalMinutes:0} minutes ({path}).",
                    ex);
            }
        }
    }

    private sealed class Release(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose()
        {
            var s = Interlocked.Exchange(ref _stream, null);
            s?.Dispose();
        }
    }
}
