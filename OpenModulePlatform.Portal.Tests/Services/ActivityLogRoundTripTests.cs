// File: OpenModulePlatform.Portal.Tests/Services/ActivityLogRoundTripTests.cs
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Services;
using Xunit;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Proves the activity log end to end against a real SQL Server: a module's
/// table created from the canonical DDL, entries written by the shared writer,
/// and read back by the Portal repository the viewer uses, with the user join,
/// the module discovery from omp.Modules, and every filter.
/// </summary>
public sealed class ActivityLogRoundTripTests : IClassFixture<ActivityLogTestFixture>
{
    private readonly ActivityLogTestFixture _fixture;

    public ActivityLogRoundTripTests(ActivityLogTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Writer_entries_come_back_through_the_repository_with_user_and_filters()
    {
        var db = _fixture.CreateConnectionFactory();
        var repo = new OmpAdminRepository(db);
        var userId = await _fixture.InsertUserAsync("Activity Tester");
        var writer = new ActivityLogWriter(db,
            new ActivityLogOptions { SchemaName = ActivityLogTestFixture.SchemaName, ModuleKey = ActivityLogTestFixture.ModuleKey, AppKey = "activity-test-web" },
            NullLogger<ActivityLogWriter>.Instance);

        await writer.WriteAsync(new ActivityEntry
        {
            Event = "thing.created",
            Summary = "Created thing 1",
            Subject = new ActivitySubject("thing", "1", "first"),
            Data = new Dictionary<string, object?> { ["size"] = 3 }
        }, userId);
        await writer.WriteAsync(new ActivityEntry
        {
            Event = "thing.deleted",
            Summary = "Deleted thing 1",
            Actor = new ActivityActor(ActivityActorKinds.System, "sweeper")
        }, ompUserId: null);

        var modules = await repo.GetActivityLogModulesAsync(CancellationToken.None);
        var module = Assert.Single(modules, m => m.ModuleKey == ActivityLogTestFixture.ModuleKey);
        Assert.Equal(ActivityLogTestFixture.SchemaName, module.SchemaName);

        var all = await repo.SearchActivityLogAsync(modules, new ActivityLogFilter(), CancellationToken.None);
        Assert.Equal(2, all.Count);
        Assert.Equal("thing.deleted", ActivityLogJson.TryParse(all[0].Entry)!.Event);
        Assert.Null(all[0].OmpUserId);
        Assert.Equal(userId, all[1].OmpUserId);
        Assert.Equal("Activity Tester", all[1].UserDisplayName);
        Assert.Equal(ActivityLogTestFixture.ModuleKey, all[1].ModuleKey);

        var byUser = await repo.SearchActivityLogAsync(modules, new ActivityLogFilter { UserId = userId }, CancellationToken.None);
        Assert.Single(byUser);
        Assert.Equal("Created thing 1", ActivityLogJson.TryParse(byUser[0].Entry)!.Summary);
        Assert.Equal(3, ActivityLogJson.TryParse(byUser[0].Entry)!.Data!.Value.GetProperty("size").GetInt32());

        var byText = await repo.SearchActivityLogAsync(modules, new ActivityLogFilter { Text = "delete" }, CancellationToken.None);
        Assert.Single(byText);

        var byEvent = await repo.SearchActivityLogAsync(modules, new ActivityLogFilter { Text = "thing.cre" }, CancellationToken.None);
        Assert.Single(byEvent);

        var future = await repo.SearchActivityLogAsync(modules, new ActivityLogFilter { FromUtc = DateTime.UtcNow.AddMinutes(5) }, CancellationToken.None);
        Assert.Empty(future);

        var otherModule = await repo.SearchActivityLogAsync(modules, new ActivityLogFilter { ModuleKeys = ["no_such_module"] }, CancellationToken.None);
        Assert.Empty(otherModule);

        var capped = await repo.SearchActivityLogAsync(modules, new ActivityLogFilter { Take = 1 }, CancellationToken.None);
        Assert.Single(capped);
    }

    [Fact]
    public async Task Writer_refuses_entries_without_event_or_summary()
    {
        var writer = new ActivityLogWriter(_fixture.CreateConnectionFactory(),
            new ActivityLogOptions { SchemaName = ActivityLogTestFixture.SchemaName, ModuleKey = ActivityLogTestFixture.ModuleKey, AppKey = "activity-test-web" },
            NullLogger<ActivityLogWriter>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(new ActivityEntry { Event = "", Summary = "s" }, ompUserId: null));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(new ActivityEntry { Event = "a.b", Summary = " " }, ompUserId: null));
    }
}

/// <summary>
/// A test database provisioned from the real core setup script (so omp.Modules
/// and omp.users are the shipped shape), plus one module schema with an
/// ActivityLog table created from the canonical DDL.
/// </summary>
public sealed class ActivityLogTestFixture : IAsyncLifetime
{
    public const string SchemaName = "omp_activity_test";
    public const string ModuleKey = "activity_test";

    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("ActivityLog");

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public SqlConnectionFactory CreateConnectionFactory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OmpDb"] = ConnectionString })
            .Build();
        return new SqlConnectionFactory(configuration);
    }

    public async Task InitializeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            master.ConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        foreach (var batch in SplitBatches(ReadCoreSetupScript()))
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var schema = new SqlCommand($"IF SCHEMA_ID(N'{SchemaName}') IS NULL EXEC(N'CREATE SCHEMA [{SchemaName}]');", conn))
        {
            await schema.ExecuteNonQueryAsync();
        }

        await using (var table = new SqlCommand(ActivityLogSql.CreateTableStatement(SchemaName), conn))
        {
            await table.ExecuteNonQueryAsync();
        }

        await using (var module = new SqlCommand(
            "INSERT INTO omp.Modules (ModuleKey, DisplayName, ModuleType, SchemaName, IsEnabled) VALUES (@key, N'Activity test', N'module', @schema, 1);", conn))
        {
            module.Parameters.AddWithValue("@key", ModuleKey);
            module.Parameters.AddWithValue("@schema", SchemaName);
            await module.ExecuteNonQueryAsync();
        }
    }

    public async Task<int> InsertUserAsync(string displayName)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("INSERT INTO omp.users (display_name) VALUES (@name); SELECT CAST(SCOPE_IDENTITY() AS int);", conn);
        cmd.Parameters.AddWithValue("@name", displayName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task DisposeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(master.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];", conn);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException)
        {
            // Best-effort cleanup.
        }
    }

    private static string ReadCoreSetupScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "OpenModulePlatform.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
        }

        var sql = File.ReadAllText(Path.Join(directory.FullName, "sql", "1-setup-openmoduleplatform.sql"));
        return Regex.Replace(
            sql,
            @"^\s*USE\s+\[OpenModulePlatform\]\s*;\s*\r?\n\s*GO\s*(?:--.*)?\s*(?:\r?\n)?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    private static IEnumerable<string> SplitBatches(string sql)
        => Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline).Where(batch => !string.IsNullOrWhiteSpace(batch));
}
