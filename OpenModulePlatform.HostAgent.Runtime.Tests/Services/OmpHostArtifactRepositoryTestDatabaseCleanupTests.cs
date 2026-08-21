using Microsoft.Data.SqlClient;
using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// Tests the cleanup machinery of <see cref="OmpHostArtifactRepositoryTestDatabase"/>
/// itself: constructor-failure cleanup, the stale-database sweep's owner-liveness
/// rules, and the cleanup-failure log channel.
/// </summary>
public sealed class OmpHostArtifactRepositoryTestDatabaseCleanupTests
{
    private static string BaseConnectionString => OmpHostArtifactRepositoryTestDatabase.GetBaseConnectionString();

    [Fact]
    public void ConstructorFailureAfterCreateLeavesNoDatabase()
    {
        string? databaseName = null;

        Assert.Throws<InvalidOperationException>(() =>
            new OmpHostArtifactRepositoryTestDatabase(name =>
            {
                databaseName = name;
                throw new InvalidOperationException("Simulated failure after CREATE DATABASE.");
            }));

        Assert.NotNull(databaseName);
        Assert.False(DatabaseExists(databaseName), $"Half-created database '{databaseName}' was left behind.");
    }

    [Fact]
    public void SweepKeepsDatabaseOwnedByLiveProcess()
    {
        // Simulates a concurrent test process on this machine: a database tagged with a
        // live owner, holding an OPEN connection -- exactly what SET SINGLE_USER WITH
        // ROLLBACK IMMEDIATE would kill mid-test if the sweep dropped it.
        var databaseName = OmpHostArtifactRepositoryTestDatabase.BuildDatabaseName(
            Environment.MachineName, Environment.ProcessId, DateTime.Now.Ticks, Guid.NewGuid());
        CreateRawDatabase(databaseName);

        var connectionString = new SqlConnectionStringBuilder(BaseConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;
        using var openConnection = new SqlConnection(connectionString);
        openConnection.Open();

        try
        {
            OmpHostArtifactRepositoryTestDatabase.SweepStaleDatabases(
                BaseConnectionString, (_, _) => true);

            Assert.True(DatabaseExists(databaseName), $"Live-owned database '{databaseName}' was swept.");

            // The connection must still work: a swept database would have kicked it.
            using var cmd = new SqlCommand("SELECT 1;", openConnection);
            Assert.Equal(1, cmd.ExecuteScalar());
        }
        finally
        {
            DropDatabaseIfExists(databaseName);
        }
    }

    [Fact]
    public void SweepDropsDatabaseWhoseOwnerIsDead()
    {
        const int deadPid = 42;
        var databaseName = OmpHostArtifactRepositoryTestDatabase.BuildDatabaseName(
            Environment.MachineName, deadPid, DateTime.Now.Ticks, Guid.NewGuid());
        CreateRawDatabase(databaseName);

        try
        {
            // Fake death only for our own fake PID; everything else on the server
            // (parallel fixtures in this very process) must be treated as alive.
            OmpHostArtifactRepositoryTestDatabase.SweepStaleDatabases(
                BaseConnectionString, (pid, _) => pid != deadPid);

            Assert.False(DatabaseExists(databaseName), $"Orphaned database '{databaseName}' was not swept.");
        }
        finally
        {
            DropDatabaseIfExists(databaseName);
        }
    }

    [Fact]
    public void SweepKeepsRecentDatabaseWithUnidentifiableOwner()
    {
        // Legacy name format (no owner tag), created just now: the 24-hour age rule
        // must keep it, because its owner could be a live process running older code.
        var databaseName = $"OmpHostAgentTests_{Guid.NewGuid():N}";
        CreateRawDatabase(databaseName);

        try
        {
            OmpHostArtifactRepositoryTestDatabase.SweepStaleDatabases(
                BaseConnectionString, (_, _) => true);

            Assert.True(DatabaseExists(databaseName), $"Recent untagged database '{databaseName}' was swept.");
        }
        finally
        {
            DropDatabaseIfExists(databaseName);
        }
    }

    [Theory]
    [InlineData(true, 0, false)] // live owner, brand new: keep
    [InlineData(true, 30, false)] // live owner, absurdly old: still keep
    [InlineData(false, 0, true)] // dead owner, brand new: reclaim the crashed-run leak
    [InlineData(false, 30, true)] // dead owner, old: reclaim
    public void ShouldSweepSameMachineOwnerFollowsLivenessNotAge(bool alive, int ageHours, bool expected)
    {
        var name = OmpHostArtifactRepositoryTestDatabase.BuildDatabaseName(
            Environment.MachineName, 424242, DateTime.Now.Ticks, Guid.NewGuid());
        var serverNow = new DateTime(2026, 8, 21, 12, 0, 0);

        var result = OmpHostArtifactRepositoryTestDatabase.ShouldSweep(
            name, serverNow - TimeSpan.FromHours(ageHours), serverNow, (_, _) => alive, out _);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ShouldSweepIndeterminateOwnerFallsBackToAgeRule()
    {
        var name = OmpHostArtifactRepositoryTestDatabase.BuildDatabaseName(
            Environment.MachineName, 424242, DateTime.Now.Ticks, Guid.NewGuid());
        var serverNow = new DateTime(2026, 8, 21, 12, 0, 0);

        Assert.False(OmpHostArtifactRepositoryTestDatabase.ShouldSweep(
            name, serverNow - TimeSpan.FromHours(1), serverNow, (_, _) => null, out _));
        Assert.True(OmpHostArtifactRepositoryTestDatabase.ShouldSweep(
            name, serverNow - TimeSpan.FromHours(25), serverNow, (_, _) => null, out _));
    }

    [Fact]
    public void ShouldSweepForeignOwnerFallsBackToAgeRule()
    {
        var name = OmpHostArtifactRepositoryTestDatabase.BuildDatabaseName(
            "OTHERMACHINE", 424242, DateTime.Now.Ticks, Guid.NewGuid());
        var serverNow = new DateTime(2026, 8, 21, 12, 0, 0);

        Assert.False(OmpHostArtifactRepositoryTestDatabase.ShouldSweep(
            name, serverNow - TimeSpan.FromHours(1), serverNow, (_, _) => true, out _));
        Assert.True(OmpHostArtifactRepositoryTestDatabase.ShouldSweep(
            name, serverNow - TimeSpan.FromHours(25), serverNow, (_, _) => true, out _));
    }

    [Fact]
    public void ShouldSweepNeverConsultsLivenessForUntaggedNames()
    {
        var serverNow = new DateTime(2026, 8, 21, 12, 0, 0);

        Assert.False(OmpHostArtifactRepositoryTestDatabase.ShouldSweep(
            $"OmpHostAgentTests_{Guid.NewGuid():N}",
            serverNow - TimeSpan.FromHours(1),
            serverNow,
            (_, _) => throw new InvalidOperationException("must not be called for untagged names"),
            out _));
    }

    [Fact]
    public void TryParseOwnerRoundTrips()
    {
        var name = OmpHostArtifactRepositoryTestDatabase.BuildDatabaseName(
            "MY_MACHINE", 1234, 638912345678901234L, Guid.NewGuid());

        Assert.True(OmpHostArtifactRepositoryTestDatabase.TryParseOwner(
            name, out var machine, out var pid, out var ticks));
        Assert.Equal("MY_MACHINE", machine);
        Assert.Equal(1234, pid);
        Assert.Equal(638912345678901234L, ticks);
    }

    [Fact]
    public void TryParseOwnerRejectsLegacyNames()
    {
        Assert.False(OmpHostArtifactRepositoryTestDatabase.TryParseOwner(
            $"OmpHostAgentTests_{Guid.NewGuid():N}", out _, out _, out _));
    }

    [Fact]
    public void RecordCleanupFailureAppendsToLogFile()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"OmpHostAgentTests-cleanup-test-{Guid.NewGuid():N}.log");
        var previous = Environment.GetEnvironmentVariable("OMP_TEST_CLEANUP_LOG");
        var marker = $"cleanup-log-marker-{Guid.NewGuid():N}";

        try
        {
            Environment.SetEnvironmentVariable("OMP_TEST_CLEANUP_LOG", logPath);

            OmpHostArtifactRepositoryTestDatabase.RecordCleanupFailure(marker);

            Assert.True(File.Exists(logPath), "Cleanup log file was not created.");
            Assert.Contains(marker, File.ReadAllText(logPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMP_TEST_CLEANUP_LOG", previous);
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
    }

    private static bool DatabaseExists(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(BaseConnectionString)
        {
            InitialCatalog = "master"
        };
        using var conn = new SqlConnection(builder.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand("SELECT COUNT(1) FROM sys.databases WHERE name = @name;", conn);
        cmd.Parameters.AddWithValue("@name", databaseName);
        return (int)cmd.ExecuteScalar()! > 0;
    }

    private static void CreateRawDatabase(string databaseName)
    {
        OmpTestDatabaseProvisioner.CreateDatabase(
            BaseConnectionString,
            $"CREATE DATABASE [{databaseName}] COLLATE Latin1_General_100_CI_AS_SC_UTF8;");
    }

    private static void DropDatabaseIfExists(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(BaseConnectionString)
        {
            InitialCatalog = "master"
        };
        using var conn = new SqlConnection(builder.ConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(
            $@"
IF DB_ID(@name) IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END;",
            conn);
        cmd.Parameters.AddWithValue("@name", databaseName);
        cmd.ExecuteNonQuery();
    }
}
