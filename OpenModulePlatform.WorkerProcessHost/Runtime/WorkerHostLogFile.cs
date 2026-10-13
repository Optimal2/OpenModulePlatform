// File: OpenModulePlatform.WorkerProcessHost/Runtime/WorkerHostLogFile.cs
namespace OpenModulePlatform.WorkerProcessHost.Runtime;

/// <summary>
/// The file name of the worker process host's default NLog file target (see
/// Program.cs, ConfigureDefaultNLog). WorkerManager starts one host process per
/// worker instance from the same provisioned folder, so the name carries the
/// process id: each process writes its own file, and no process can lose its
/// lines to another that opened a shared file first. The date comes first so
/// files sort by day and the 14-day cleanup (MaxArchiveDays) matches every
/// process's files, including the single-file name used before this change.
/// </summary>
public static class WorkerHostLogFile
{
    public const string DefaultFileName = "OpenModulePlatform.WorkerProcessHost-${shortdate}-${processid}.log";

    /// <summary>How long a log file is kept (the target's MaxArchiveDays).</summary>
    public const int RetentionDays = 14;

    private const string FilePattern = "OpenModulePlatform.WorkerProcessHost-*.log";

    /// <summary>
    /// Deletes this host's log files older than <paramref name="retention"/>
    /// (by last write) from <paramref name="directory"/>; returns how many were
    /// deleted. NLog's MaxArchiveDays only cleans when a RUNNING process rolls
    /// over to a new date, and then only files of its own name (the process id
    /// is fixed in it), so the files of processes that have exited would stay
    /// for ever; it did not clean at startup with the old single-file name
    /// either (measured 2026-10-13). Every host process therefore cleans the
    /// shared directory when it starts. Only files matching this host's own
    /// name pattern are touched, never anything else in the directory; a file
    /// another process still writes, or one that cannot be deleted, is skipped.
    /// </summary>
    public static int DeleteExpiredFiles(string directory, DateTime utcNow, TimeSpan retention)
    {
        var deleted = 0;
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(directory, FilePattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (var file in candidates)
        {
            try
            {
                if (utcNow - File.GetLastWriteTimeUtc(file) > retention)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or not ours to delete: the next start tries again.
            }
        }

        return deleted;
    }

    /// <summary>
    /// The worker instance key from the host's command line
    /// (<c>--WorkerProcess:WorkerInstanceKey=...</c>), read before the host is
    /// built so the first line of the process's file can name its worker.
    /// Empty when absent.
    /// </summary>
    public static string WorkerInstanceKeyFromArgs(IReadOnlyList<string> args)
    {
        const string prefix = "--WorkerProcess:WorkerInstanceKey";
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(prefix.Length + 1)..];
            }

            if (string.Equals(arg, prefix, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                return args[i + 1];
            }
        }

        return string.Empty;
    }
}
