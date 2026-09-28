using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Services;
using System.Text;
using System.Text.Json;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class DashboardWidgetReadinessTests : IAsyncLifetime
{
    private static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("WidgetReadiness");
    private readonly string _connectionString = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(master.ConnectionString,
            $"CREATE DATABASE [{DatabaseName}];");
    }

    public async Task DisposeAsync()
    {
        var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(master.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];", conn);
        try { await cmd.ExecuteNonQueryAsync(); }
        catch (SqlException ex) { OmpTestCleanupLog.RecordFailure(nameof(DashboardWidgetReadinessTests), ex.Message); }
    }

    [Fact]
    public async Task Readiness_FindsLegacyPayloadsIncludingDisabledRows_AndClearsAfterSameVersionRepair()
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var setup = new SqlCommand("""
            IF SCHEMA_ID(N'omp_portal') IS NULL EXEC(N'CREATE SCHEMA omp_portal');
            IF SCHEMA_ID(N'omp') IS NULL EXEC(N'CREATE SCHEMA omp');
            CREATE TABLE omp.Permissions (PermissionId int PRIMARY KEY, Name nvarchar(200));
            CREATE TABLE omp.Roles (RoleId int PRIMARY KEY, Name nvarchar(200));
            CREATE TABLE omp_portal.widgets
            (
                widget_id int NOT NULL PRIMARY KEY, widget_key nvarchar(200) NULL,
                widget_version nvarchar(50) NULL, widget_type nvarchar(50) NOT NULL,
                payload nvarchar(4000) NULL, is_enabled bit NOT NULL,
                title nvarchar(200) NOT NULL DEFAULT N'Example', description nvarchar(1000) NULL,
                module_key nvarchar(100) NULL, author nvarchar(200) NULL,
                modified_at datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
            );
            CREATE TABLE omp_portal.widget_permissions (widget_id int NOT NULL, permission_id int NULL, role_id int NULL);
            INSERT omp_portal.widgets (widget_id, widget_key, widget_version, widget_type, payload, is_enabled) VALUES
              (1, N'example:null', N'1.0.0', N'module-fragment', NULL, 1),
              (2, N'example:json', N'1.0.0', N'module-fragment', N'not json', 1),
              (3, N'example:fields', N'1.0.0', N'module-fragment', N'{}', 0),
              (4, N'example:valid', N'1.0.0', N'module-fragment', N'{"appKey":"example_web","fragmentPath":"/widgets/overview"}', 1),
              (5, N'example:portal', N'1.0.0', N'portal', N'admin-overview', 1),
              (6, N'example:path', N'1.0.0', N'module-fragment', N'{"appKey":"example_web","fragmentPath":"//invalid.example/"}', 1);
            """, conn);
        await setup.ExecuteNonQueryAsync();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:OmpDb"] = _connectionString }).Build();
        var repository = new OmpAdminRepository(new SqlConnectionFactory(configuration));
        var issues = await repository.GetDashboardWidgetReadinessIssuesAsync(CancellationToken.None);
        Assert.Equal(new[] { 1, 2, 3, 6 }, issues.Select(issue => issue.WidgetId));
        Assert.All(issues, issue => Assert.Equal("1.0.0", issue.WidgetVersion));
        Assert.Contains(issues, issue => issue.WidgetKey == "example:fields" && !issue.IsEnabled);

        var json = JsonSerializer.Serialize(new
        {
            format = "omp.portal.dashboard.widgets", formatVersion = 1,
            widgets = issues.Select(issue => new
            {
                widgetKey = issue.WidgetKey, widgetVersion = issue.WidgetVersion, title = "Example",
                widgetType = "module-fragment", appKey = "example_web", fragmentPath = "/widgets/overview",
                permissionNames = Array.Empty<string>(), roleNames = Array.Empty<string>()
            })
        });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var imported = await new PortalDashboardWidgetPackageService(new SqlConnectionFactory(configuration))
            .ImportAsync(stream, "example.widgets.json", replaceExistingWidgets: true, quickImport: false, CancellationToken.None);
        Assert.Equal(4, imported.UpdatedCount);
        Assert.Empty(await repository.GetDashboardWidgetReadinessIssuesAsync(CancellationToken.None));
        await using var unchanged = new SqlCommand(
            "SELECT COUNT(*) FROM omp_portal.widgets WHERE widget_version = N'1.0.0' AND (widget_id <> 3 OR is_enabled = 0);", conn);
        Assert.Equal(6, (int)(await unchanged.ExecuteScalarAsync())!);
    }
}
