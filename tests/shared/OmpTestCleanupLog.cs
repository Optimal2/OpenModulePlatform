namespace OpenModulePlatform.TestSupport;

/// <summary>
/// The one channel for test-database cleanup failures: a drop that failed in a
/// fixture's dispose, or a stale-database sweep that could not complete.
/// </summary>
/// <remarks>
/// <para>
/// Console.Error is captured by xUnit and attached to whichever test happens to be
/// running, and for a PASSING test it only surfaces at detailed verbosity -- invisible
/// in the CI output actually run. So every failure also goes to a log file that the CI
/// step "Surface test-database cleanup failures" turns into workflow warnings regardless
/// of any test outcome. The path is <c>OMP_TEST_CLEANUP_LOG</c> when set (CI points it
/// under TestResults), otherwise a file in the temp directory.
/// </para>
/// <para>
/// Until 2026-09-21 only the HostAgent fixture wrote here; the Portal sweep reported to
/// Console.Error alone and the Portal fixtures swallowed a failed drop without a line,
/// so a leaked <c>OpenModulePlatform_PortalTests_*</c> database was invisible until
/// someone listed sys.databases by hand. Both test projects link this file.
/// </para>
/// </remarks>
public static class OmpTestCleanupLog
{
    /// <summary>
    /// Where cleanup failures are appended. Overridable via
    /// <c>OMP_TEST_CLEANUP_LOG</c>; CI sets it to a file under TestResults.
    /// </summary>
    public static string GetPath()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("OMP_TEST_CLEANUP_LOG");
        return string.IsNullOrWhiteSpace(fromEnvironment)
            ? Path.Join(Path.GetTempPath(), "OmpTests-cleanup.log")
            : fromEnvironment;
    }

    /// <summary>
    /// Reports a cleanup failure through every channel available: Console.Error for the
    /// running test's output and the log file for the CI warning step.
    /// </summary>
    /// <param name="source">A short tag naming the reporter, e.g. the fixture class.</param>
    /// <param name="message">What failed; include the database name and the error.</param>
    public static void RecordFailure(string source, string message)
    {
        var line = $"[{source}] [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [pid {Environment.ProcessId}] {message}";
        Console.Error.WriteLine(line);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var path = GetPath();
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(path, line + Environment.NewLine);
                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
            {
                // Parallel test assemblies can append at the same moment; retry briefly.
                Thread.Sleep(50 * attempt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A lost log line must never fail a test run.
                Console.Error.WriteLine($"[{source}] Could not write to the cleanup log: {ex.Message}");
                return;
            }
        }
    }
}
