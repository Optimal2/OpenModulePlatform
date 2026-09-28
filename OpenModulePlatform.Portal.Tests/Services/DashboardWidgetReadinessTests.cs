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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortalImport_ClosesSkippedFindingsOnlyForStoredKeys(bool updateExisting)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var setup = new SqlCommand("""
            IF SCHEMA_ID(N'omp_portal') IS NULL EXEC(N'CREATE SCHEMA omp_portal');
            IF SCHEMA_ID(N'omp') IS NULL EXEC(N'CREATE SCHEMA omp');
            CREATE TABLE omp_portal.widgets
            (
                widget_id int IDENTITY(1,1) NOT NULL PRIMARY KEY, widget_key nvarchar(200) NULL,
                widget_version nvarchar(50) NULL, widget_type nvarchar(50) NOT NULL,
                payload nvarchar(4000) NULL, is_enabled bit NOT NULL DEFAULT 1,
                title nvarchar(200) NOT NULL DEFAULT N'Example', description nvarchar(1000) NULL,
                module_key nvarchar(100) NULL, author nvarchar(200) NULL,
                modified_at datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
            );
            CREATE TABLE omp.MaintenanceFindings
            (
                MaintenanceFindingId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
                FindingKey nvarchar(450) NOT NULL UNIQUE, Category nvarchar(100) NOT NULL,
                TargetIdentifier nvarchar(1000) NOT NULL, Detail nvarchar(max) NULL,
                Status tinyint NOT NULL, LastSeenUtc datetime2(3) NOT NULL,
                ResultMessage nvarchar(max) NULL, UpdatedUtc datetime2(3) NULL
            );
            CREATE TABLE omp.Permissions (PermissionId int PRIMARY KEY, Name nvarchar(200));
            CREATE TABLE omp.Roles (RoleId int PRIMARY KEY, Name nvarchar(200));
            CREATE TABLE omp_portal.widget_permissions (widget_id int NOT NULL, permission_id int NULL, role_id int NULL);
            INSERT omp_portal.widgets (widget_key, widget_version, widget_type, payload, is_enabled, modified_at) VALUES
              (N'example:valid', N'2.0.0', N'portal', N'admin-overview', 1, '2026-01-01');
            INSERT omp.MaintenanceFindings (FindingKey, Category, TargetIdentifier, Detail, Status, LastSeenUtc) VALUES
              (N'DashboardWidgetImportSkipped:example:future', N'DashboardWidgetImportSkipped', N'example:future',
               N'unsupported widgetType future-widget', 0, '2026-02-01'),
              (N'DashboardWidgetImportSkipped:example:ignored', N'DashboardWidgetImportSkipped', N'example:ignored', N'ignored', 1, '2026-02-01'),
              (N'Other:example:future', N'HostAgentLeftover', N'example:future', N'other category', 0, '2026-02-01'),
              (N'failed', N'DashboardWidgetImportSkipped', N'example:future', N'failed', 4, '2026-02-01'),
              (N'ignored', N'DashboardWidgetImportSkipped', N'example:future', N'ignored', 1, '2026-02-01');
            """, conn);
        await setup.ExecuteNonQueryAsync();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:OmpDb"] = _connectionString }).Build();
        var repository = new OmpAdminRepository(new SqlConnectionFactory(configuration));

        var skipped = Assert.Single(await repository.GetDashboardWidgetReadinessIssuesAsync(CancellationToken.None));
        Assert.True(skipped.IsSkippedImport);
        Assert.Equal("example:future", skipped.WidgetKey);
        Assert.Equal(0, skipped.WidgetId);
        Assert.Equal("unsupported widgetType future-widget", skipped.SkipReason);
        Assert.Equal(new DateTime(2026, 2, 1), skipped.SkippedUtc);

        if (updateExisting)
        {
            await using var existing = new SqlCommand("""
                INSERT omp_portal.widgets (widget_key, widget_version, widget_type, payload, is_enabled)
                VALUES (N'example:future', N'0.0.0', N'portal', NULL, 1);
                """, conn);
            await existing.ExecuteNonQueryAsync();
            // An unrelated row write must not silently hide an open finding.
            Assert.Single(await repository.GetDashboardWidgetReadinessIssuesAsync(CancellationToken.None));
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"format":"omp.portal.dashboard.widgets","formatVersion":1,"widgets":[
              {"widgetKey":"example:future","widgetVersion":"1.0.0","title":"Example","widgetType":"portal",
               "payload":"admin-overview","permissionNames":[],"roleNames":[]}]}
            """));
        var result = await new PortalDashboardWidgetPackageService(new SqlConnectionFactory(configuration))
            .ImportAsync(stream, "example.widgets.json", replaceExistingWidgets: true, quickImport: false, CancellationToken.None);
        Assert.Equal(1, result.CreatedCount + result.UpdatedCount);
        Assert.Empty(await repository.GetDashboardWidgetReadinessIssuesAsync(CancellationToken.None));
        await using var status = new SqlCommand("""
            SELECT COUNT(*) FROM omp.MaintenanceFindings
            WHERE Category = N'DashboardWidgetImportSkipped' AND TargetIdentifier = N'example:future'
              AND Status = 3 AND ResultMessage IS NOT NULL AND UpdatedUtc IS NOT NULL;
            """, conn);
        Assert.Equal(2, (int)(await status.ExecuteScalarAsync())!);
        await using var preserved = new SqlCommand("""
            SELECT COUNT(*) FROM omp.MaintenanceFindings
            WHERE (Status = 1) OR (Category = N'HostAgentLeftover' AND Status = 0);
            """, conn);
        Assert.Equal(3, (int)(await preserved.ExecuteScalarAsync())!);

        await using var reopen = new SqlCommand("""
            UPDATE omp.MaintenanceFindings SET Status = 0 WHERE Status = 3;
            INSERT omp.MaintenanceFindings (FindingKey, Category, TargetIdentifier, Detail, Status, LastSeenUtc)
            VALUES (N'newer', N'DashboardWidgetImportSkipped', N'example:valid', N'keep newer widget', 0, '2026-02-01');
            """, conn);
        await reopen.ExecuteNonQueryAsync();
        using var olderStream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"format":"omp.portal.dashboard.widgets","formatVersion":1,"widgets":[
              {"widgetKey":"example:valid","widgetVersion":"1.0.0","title":"Example","widgetType":"portal",
               "payload":"admin-overview","permissionNames":[],"roleNames":[]}]}
            """));
        var service = new PortalDashboardWidgetPackageService(new SqlConnectionFactory(configuration));
        var skippedResult = await service.ImportAsync(olderStream, "example.widgets.json", false, true, CancellationToken.None);
        Assert.Equal(1, skippedResult.SkippedCount);
        Assert.Equal(3, (await repository.GetDashboardWidgetReadinessIssuesAsync(CancellationToken.None)).Count);

        // The first widget would resolve its findings, but the second causes the entire import to roll back.
        using var failingStream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"format":"omp.portal.dashboard.widgets","formatVersion":1,"widgets":[
              {"widgetKey":"example:future","widgetVersion":"2.0.0","title":"Example","widgetType":"portal",
               "payload":"admin-overview","permissionNames":[],"roleNames":[]},
              {"widgetKey":"example:valid","widgetVersion":"2.0.0","title":"Changed","widgetType":"portal",
               "payload":"admin-overview","permissionNames":[],"roleNames":[]}]}
            """));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(
            failingStream, "example.widgets.json", false, false, CancellationToken.None));
        Assert.Equal(3, (await repository.GetDashboardWidgetReadinessIssuesAsync(CancellationToken.None)).Count);
        Assert.Equal(0, (int)(await status.ExecuteScalarAsync())!);
    }
}
