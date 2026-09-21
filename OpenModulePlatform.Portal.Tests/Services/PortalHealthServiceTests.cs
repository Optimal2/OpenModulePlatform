using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// The service behind <c>/health/live</c> and <c>/health/ready</c>. Until 2026-09-21 it
/// had no test at all, although a load balancer acts on its answer: a readiness probe
/// that throws instead of answering 503 takes the instance out of rotation for the wrong
/// reason, and one that answers 200 without the schema check lets a half-migrated
/// instance serve traffic.
/// </summary>
public sealed class PortalHealthServiceTests
{
    [Fact]
    public void Liveness_is_always_healthy_and_names_the_machine()
    {
        var service = CreateService(new ConfigurationBuilder().Build());
        var before = DateTime.UtcNow;

        var result = service.CheckLive();

        Assert.True(result.IsHealthy);
        Assert.Equal(Environment.MachineName, result.MachineName);
        Assert.InRange(result.CheckedUtc, before, DateTime.UtcNow);
    }

    /// <summary>
    /// The probe must never throw: a missing connection string is the "configuration
    /// broken" case and has to come back as an unhealthy answer the endpoint turns into 503.
    /// </summary>
    [Fact]
    public async Task Readiness_without_a_connection_string_reports_unhealthy_instead_of_throwing()
    {
        var service = CreateService(new ConfigurationBuilder().Build());

        var result = await service.CheckReadyAsync(CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.False(result.DatabaseOk);
        Assert.Equal(Environment.MachineName, result.MachineName);
        Assert.Contains("ConnectionStrings:OmpDb", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reachable database whose omp/omp_portal schema is missing is the state right after
    /// a failed module apply. The probe must answer "database ok, not ready" with the repair
    /// hint, not "ready".
    /// </summary>
    [Fact]
    public async Task Readiness_against_a_reachable_database_without_the_schema_reports_database_ok_but_not_ready()
    {
        var databaseName = OmpTestDatabaseNames.ForPortalTests("Health");
        var connectionString = TestSqlConnection.ForDatabase(databaseName);
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            master,
            $"IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{databaseName}];");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:OmpDb"] = connectionString
                })
                .Build();
            var service = CreateService(configuration);

            var result = await service.CheckReadyAsync(CancellationToken.None);

            Assert.True(result.DatabaseOk);
            Assert.False(result.IsHealthy);
            Assert.Contains("SQL repair", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            await DropDatabaseAsync(master, databaseName);
        }
    }

    private static PortalHealthService CreateService(IConfiguration configuration)
        => new(new SqlConnectionFactory(configuration), NullLogger<PortalHealthService>.Instance);

    private static async Task DropDatabaseAsync(string masterConnectionString, string databaseName)
    {
        try
        {
            await using var conn = new SqlConnection(masterConnectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];",
                conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            OmpTestCleanupLog.RecordFailure(
                nameof(PortalHealthServiceTests),
                $"Could not drop test database '{databaseName}': {ex.Message}");
        }
    }
}
