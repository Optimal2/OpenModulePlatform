using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace OpenModulePlatform.TestSupport;

/// <summary>
/// Per-process test database names for the Portal test fixtures, plus the sweep
/// that removes databases left behind by processes that no longer exist.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-09-03 every Portal fixture used a FIXED name
/// (<c>OpenModulePlatform_PortalTests_PushEvents</c> and so on). Two test hosts
/// running at the same time -- a local CI run next to a pre-push hook, a reviewer's
/// worktree next to the main tree -- therefore shared one database: one process
/// dropped it while the other was inside it ("Cannot open database", "Login failed",
/// "Cannot drop the database ... currently in use") and both seeded the same rows
/// (UNIQUE KEY violations on omp.Modules). Measured: two concurrent
/// <c>local-ci.ps1</c> runs failed 2 + 1 tests, four concurrent test hosts failed
/// 23 + 13, all in Portal.Tests, while HostAgent.Runtime.Tests -- whose fixture
/// already tags its database names with the owning process -- passed 443/443 in
/// every run.
/// </para>
/// <para>
/// The name carries the owning process (pid + real start time) the same way
/// <c>OmpHostArtifactRepositoryTestDatabase</c> does, so a concurrent run can never
/// collide and the sweep can tell a crashed run's leftover from a live neighbour.
/// The sweep runs once per process, from the provisioner, before the first
/// CREATE DATABASE.
/// </para>
/// </remarks>
public static class OmpTestDatabaseNames
{
    public const string PortalPrefix = "OpenModulePlatform_PortalTests_";

    /// <summary>Owner tag: pid + process start ticks, appended to every name.</summary>
    private static readonly int ProcessId = Environment.ProcessId;
    private static readonly long ProcessStartTicks = Process.GetCurrentProcess().StartTime.Ticks;
    private static readonly string MachineName = Environment.MachineName;

    /// <summary>Databases whose owner cannot be identified are dropped after this age.</summary>
    internal static readonly TimeSpan UnknownOwnerMaxAge = TimeSpan.FromHours(24);

    /// <summary>Process start times drift by scheduler resolution; compare with tolerance.</summary>
    private static readonly TimeSpan OwnerStartTimeTolerance = TimeSpan.FromSeconds(10);

    private static int _sweepDone;

    /// <summary>
    /// <c>OpenModulePlatform_PortalTests_{suffix}_{pid}_{startTicks}</c> -- stable for
    /// the lifetime of the process (a class fixture may create it more than once with
    /// <c>IF DB_ID(...) IS NULL</c>), unique across processes.
    /// </summary>
    public static string ForPortalTests(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix) || suffix.Contains('_', StringComparison.Ordinal))
        {
            throw new ArgumentException("suffix must be non-empty and must not contain '_'.", nameof(suffix));
        }

        return BuildPortalName(suffix, ProcessId, ProcessStartTicks);
    }

    internal static string BuildPortalName(string suffix, int processId, long processStartTicks)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{PortalPrefix}{suffix}_{processId}_{processStartTicks}");

    /// <summary>
    /// Parses the owner out of a Portal test database name. Returns false for the
    /// legacy fixed names (<c>OpenModulePlatform_PortalTests_PushEvents</c>) and for
    /// names from elsewhere; those fall back to the age rule.
    /// </summary>
    internal static bool TryParseOwner(string databaseName, out int ownerProcessId, out long ownerProcessStartTicks)
    {
        ownerProcessId = 0;
        ownerProcessStartTicks = 0;
        if (!databaseName.StartsWith(PortalPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = databaseName.Substring(PortalPrefix.Length).Split('_');
        return parts.Length == 3
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ownerProcessId)
            && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ownerProcessStartTicks);
    }

    /// <summary>
    /// The sweep decision for one candidate. Owned by a live process: never drop.
    /// Owned by a dead process: drop at any age (the crashed-run leak). Owner unknown
    /// or undeterminable: drop only when older than <see cref="UnknownOwnerMaxAge"/>,
    /// which also covers the legacy fixed names once nothing uses them any more.
    /// </summary>
    internal static bool ShouldSweep(
        string databaseName,
        DateTime createDate,
        DateTime serverNow,
        Func<int, long, bool?> isOwnerProcessAlive,
        out string reason)
    {
        if (!databaseName.StartsWith(PortalPrefix, StringComparison.Ordinal))
        {
            reason = "not a Portal test database";
            return false;
        }

        if (TryParseOwner(databaseName, out var pid, out var startTicks))
        {
            var alive = isOwnerProcessAlive(pid, startTicks);
            if (alive == true)
            {
                reason = $"owned by live process {pid}";
                return false;
            }

            if (alive == false)
            {
                reason = $"owner process {pid} is no longer running";
                return true;
            }
        }

        if (createDate < serverNow - UnknownOwnerMaxAge)
        {
            reason = $"owner not identifiable and older than {UnknownOwnerMaxAge.TotalHours:0} hours";
            return true;
        }

        reason = "owner not identifiable and younger than the age limit";
        return false;
    }

    private static bool? IsOwnerProcessAlive(int processId, long processStartTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return Math.Abs(process.StartTime.Ticks - processStartTicks) <= OwnerStartTimeTolerance.Ticks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Drops Portal test databases whose owner is gone. Once per process; every
    /// failure is reported on stderr and swallowed -- a sweep must never fail a test.
    /// </summary>
    public static void SweepStalePortalDatabasesOnce(string masterConnectionString)
    {
        if (Interlocked.Exchange(ref _sweepDone, 1) == 1)
        {
            return;
        }

        // The sweep runs as a task whose fault is observed rather than caught: the contract
        // (report on stderr, never fail a test) holds for every exception type without a
        // catch clause that would have to name them all.
        var sweep = Task.Run(() => SweepStalePortalDatabases(masterConnectionString));
        sweep.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Wait();
        if (sweep.Exception is { } failure)
        {
            Console.Error.WriteLine($"[OmpTestDatabaseNames] Sweep of stale Portal test databases failed: {failure.GetBaseException().Message}");
        }
    }

    private static void SweepStalePortalDatabases(string masterConnectionString)
    {
        var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = "master" };
        using var conn = new SqlConnection(builder.ConnectionString);
        conn.Open();

        var candidates = new List<(string Name, DateTime CreateDate, DateTime ServerNow)>();
        using (var cmd = new SqlCommand(
            "SELECT name, create_date, SYSDATETIME() FROM sys.databases WHERE name LIKE @prefix + N'%';",
            conn))
        {
            cmd.Parameters.AddWithValue("@prefix", PortalPrefix.Replace("_", "[_]", StringComparison.Ordinal));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add((reader.GetString(0), reader.GetDateTime(1), reader.GetDateTime(2)));
            }
        }

        foreach (var (name, createDate, serverNow) in candidates)
        {
            if (!ShouldSweep(name, createDate, serverNow, IsOwnerProcessAlive, out var reason))
            {
                continue;
            }

            try
            {
                using var drop = new SqlCommand(
                    $"ALTER DATABASE [{name.Replace("]", "]]", StringComparison.Ordinal)}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                    $"DROP DATABASE [{name.Replace("]", "]]", StringComparison.Ordinal)}];",
                    conn);
                drop.ExecuteNonQuery();
                Console.Error.WriteLine($"[OmpTestDatabaseNames] Dropped stale test database '{name}' ({reason}, machine {MachineName}).");
            }
            catch (SqlException ex)
            {
                Console.Error.WriteLine($"[OmpTestDatabaseNames] Could not drop stale test database '{name}': {ex.Message}");
            }
        }
    }
}
