using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Portal.Pages.Admin;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Pages;

/// <summary>
/// Admin page handlers against a database that lacks the optional tables they write to. Each
/// service reports that with a prepared message; the handlers must show it instead of letting
/// the exception reach the general error page. The dashboard widget writes must not report
/// success for an id that matches no row.
/// </summary>
public sealed class AdminPageErrorHandlingTests : IAsyncLifetime
{
    private static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("AdminPageErrors");
    private readonly string _connectionString = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(master.ConnectionString,
            $"CREATE DATABASE [{DatabaseName}];");

        // Only what the handlers read before the write under test; the banners, iFrame and
        // HostAgent job tables are deliberately absent.
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var setup = new SqlCommand("""
            IF SCHEMA_ID(N'omp_portal') IS NULL EXEC(N'CREATE SCHEMA omp_portal');
            IF SCHEMA_ID(N'omp') IS NULL EXEC(N'CREATE SCHEMA omp');
            CREATE TABLE omp.Roles (RoleId int PRIMARY KEY, Name nvarchar(200) NOT NULL);
            CREATE TABLE omp_portal.widgets
            (
                widget_id int NOT NULL PRIMARY KEY,
                is_enabled bit NOT NULL,
                description nvarchar(1000) NULL,
                modified_at datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()
            );
            INSERT omp_portal.widgets (widget_id, is_enabled) VALUES (1, 1);
            """, conn);
        await setup.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(master.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];", conn);
        try { await cmd.ExecuteNonQueryAsync(); }
        catch (SqlException ex) { OmpTestCleanupLog.RecordFailure(nameof(AdminPageErrorHandlingTests), ex.Message); }
    }

    [Fact]
    public async Task DashboardWidgetWrites_RefuseAnIdThatMatchesNoRow()
    {
        var service = new PortalDashboardWidgetPackageService(Factory());

        var enable = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetWidgetEnabledAsync(999, false, CancellationToken.None));
        var describe = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateWidgetDescriptionAsync(999, "Example", CancellationToken.None));
        Assert.Equal("The selected dashboard widget was not found.", enable.Message);
        Assert.Equal("The selected dashboard widget was not found.", describe.Message);

        // An existing row is still changed.
        await service.SetWidgetEnabledAsync(1, false, CancellationToken.None);
        await service.UpdateWidgetDescriptionAsync(1, "Example", CancellationToken.None);
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var check = new SqlCommand(
            "SELECT COUNT(*) FROM omp_portal.widgets WHERE widget_id = 1 AND is_enabled = 0 AND description = N'Example';", conn);
        Assert.Equal(1, (int)(await check.ExecuteScalarAsync())!);
    }

    [Fact]
    public Task DashboardWidgetEnableHandler_ReportsMissingWidgetInsteadOfSuccess()
        => PageHandlerHarness.InEnglishAsync(async () =>
        {
            var model = PageHandlerHarness.Prepare(new DashboardWidgetsModel(
                PageHandlerHarness.AnonymousOptions(),
                null!,
                new PortalDashboardWidgetPackageService(Factory())));

            var result = await model.OnPostSetEnabled(999, true, CancellationToken.None);

            Assert.IsType<RedirectToPageResult>(result);
            Assert.Equal("The selected dashboard widget was not found.", model.StatusMessage);
        });

    [Fact]
    public Task BannerSaveHandler_ShowsMissingTableMessage()
        => PageHandlerHarness.InEnglishAsync(async () =>
        {
            var model = PageHandlerHarness.Prepare(new BannersModel(
                PageHandlerHarness.AnonymousOptions(),
                null!,
                new BannerService(Factory(), null!, NullLogger<BannerService>.Instance),
                new OmpTime(),
                PageHandlerHarness.PortalLocalizer()));
            model.Input = new BannersModel.InputModel
            {
                Title = "Example",
                Content = "Example",
                Status = BannerService.StatusActive,
                Level = BannerService.LevelAnnouncement,
                TargetMode = "global"
            };

            var result = await model.OnPostSave(CancellationToken.None);

            Assert.IsType<PageResult>(result);
            var error = Assert.Single(model.ModelState[string.Empty]!.Errors);
            Assert.Equal("OMP banners table is not installed.", error.ErrorMessage);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task IFrameEnableHandlers_ShowMissingTablesMessage(bool urlSet)
        => PageHandlerHarness.InEnglishAsync(async () =>
        {
            var model = PageHandlerHarness.Prepare(new IFrameUrlsModel(
                PageHandlerHarness.AnonymousOptions(),
                null!,
                new IFrameAdminService(Factory())));

            var result = urlSet
                ? await model.OnPostSetUrlSetEnabled(1, true, CancellationToken.None)
                : await model.OnPostSetUrlEnabled(1, true, CancellationToken.None);

            Assert.IsType<RedirectToPageResult>(result);
            Assert.Equal("The iFrame URL tables are not installed.", model.StatusMessage);
        });

    [Fact]
    public Task MaintenanceScanHandler_ShowsMissingJobQueueMessage()
        => PageHandlerHarness.InEnglishAsync(async () =>
        {
            var model = PageHandlerHarness.Prepare(new MaintenanceModel(
                PageHandlerHarness.AnonymousOptions(),
                null!,
                new OmpAdminRepository(Factory()),
                null!,
                null!));

            var result = await model.OnPostQueueMaintenanceScan(CancellationToken.None);

            Assert.IsType<RedirectToPageResult>(result);
            Assert.Equal(
                "HostAgent job queue is not available. Apply the core OMP schema before queueing maintenance jobs.",
                model.StatusMessage);
        });

    private SqlConnectionFactory Factory()
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OmpDb"] = _connectionString })
            .Build());
}
