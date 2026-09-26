using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Integration;

/// <summary>
/// Renders dashboard widget partials through the Portal's real Razor pipeline.
/// </summary>
[Collection(PushEventPipelineTestCollection.Name)]
public sealed class DashboardWidgetRenderingIntegrationTests
{
    private const string SensitiveIdentifier = "191212121212-RENDER-PROBE";

    private readonly PushEventPipelineTestFixture _fixture;

    public DashboardWidgetRenderingIntegrationTests(PushEventPipelineTestFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The dashboard's recent-jobs widget must not show the searched identifier anywhere:
    /// not as text, not in a title and not in a data attribute. The job row in the module
    /// table carries an identifier; the rendered widget must still show the job.
    /// </summary>
    [Fact]
    public async Task RecentJobsWidget_WithJobThatHasIdentifier_DoesNotRenderTheIdentifier()
    {
        await SeedRecentJobAsync();

        using var scope = _fixture.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<PortalModuleDashboardService>();
        var httpContext = CreateHttpContext(scope.ServiceProvider);
        var model = await service.GetLogSearchWidgetAsync(
            httpContext.Request,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LogSearch.View" },
            CancellationToken.None);

        Assert.NotEmpty(model.Jobs);

        var html = await RenderPartialAsync(
            scope.ServiceProvider,
            httpContext,
            "/Pages/Shared/_DashboardLogSearchWidget.cshtml",
            model);

        Assert.Contains("dashboard-module-widget__item", html, StringComparison.Ordinal);
        Assert.Contains("/JobDetails/", html, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveIdentifier, html, StringComparison.Ordinal);
        Assert.DoesNotContain("RENDER-PROBE", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModuleFragmentWidget_Unavailable_RendersNeutralPlaceholder()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var httpContext = CreateHttpContext(scope.ServiceProvider);

        var html = await RenderPartialAsync(
            scope.ServiceProvider,
            httpContext,
            "/Pages/Shared/_DashboardModuleFragmentWidget.cshtml",
            new DashboardModuleFragmentWidget(7, 300, ModuleFragmentResult.Unavailable));

        Assert.Contains("dashboard-module-fragment__placeholder", html, StringComparison.Ordinal);
        Assert.Contains("is-unavailable", html, StringComparison.Ordinal);
        Assert.Contains("is-narrow", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModuleFragmentWidget_Loaded_RendersSanitizedContentWithModeClass()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var httpContext = CreateHttpContext(scope.ServiceProvider);
        var fragment = ModuleFragmentHtmlSanitizer.Sanitize(
            "<div data-widget-mode=\"medium\"><strong>42</strong><script>x()</script></div>",
            "/sample");

        var html = await RenderPartialAsync(
            scope.ServiceProvider,
            httpContext,
            "/Pages/Shared/_DashboardModuleFragmentWidget.cshtml",
            new DashboardModuleFragmentWidget(7, 900, fragment));

        Assert.Contains("<strong>42</strong>", html, StringComparison.Ordinal);
        Assert.Contains("is-medium", html, StringComparison.Ordinal);
        Assert.DoesNotContain("is-wide", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
    }

    private async Task SeedRecentJobAsync()
    {
        const string sql = """
            IF SCHEMA_ID(N'omp_log_search') IS NULL EXEC(N'CREATE SCHEMA omp_log_search');
            IF OBJECT_ID(N'omp_log_search.SearchJobs', N'U') IS NULL
            BEGIN
                CREATE TABLE omp_log_search.SearchJobs
                (
                    SearchJobId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    PersonIdentifier nvarchar(100) NOT NULL,
                    Status tinyint NOT NULL,
                    RequestedUtc datetime2(3) NOT NULL,
                    CompletedUtc datetime2(3) NULL,
                    HitCount int NOT NULL,
                    ErrorCount int NOT NULL
                );
            END;

            IF OBJECT_ID(N'omp.Apps', N'U') IS NULL
                CREATE TABLE omp.Apps(AppId int NOT NULL PRIMARY KEY, AppKey nvarchar(100) NOT NULL, AppType nvarchar(50) NOT NULL);
            IF OBJECT_ID(N'omp.Hosts', N'U') IS NULL
                CREATE TABLE omp.Hosts(HostId uniqueidentifier NOT NULL PRIMARY KEY, HostKey nvarchar(100) NOT NULL, BaseUrl nvarchar(300) NULL);
            IF OBJECT_ID(N'omp.Permissions', N'U') IS NULL
                CREATE TABLE omp.Permissions(PermissionId int NOT NULL PRIMARY KEY, Name nvarchar(200) NOT NULL);
            IF OBJECT_ID(N'omp.AppPermissions', N'U') IS NULL
                CREATE TABLE omp.AppPermissions(AppId int NOT NULL, PermissionId int NOT NULL, RequireAll bit NOT NULL);
            IF OBJECT_ID(N'omp.AppInstances', N'U') IS NULL
                CREATE TABLE omp.AppInstances
                (
                    AppInstanceId uniqueidentifier NOT NULL PRIMARY KEY,
                    AppInstanceKey nvarchar(100) NOT NULL,
                    AppId int NOT NULL,
                    DisplayName nvarchar(200) NOT NULL,
                    RoutePath nvarchar(300) NULL,
                    PublicUrl nvarchar(300) NULL,
                    HostId uniqueidentifier NULL,
                    SortOrder int NOT NULL,
                    Description nvarchar(1000) NULL,
                    IsEnabled bit NOT NULL,
                    IsAllowed bit NOT NULL
                );

            INSERT INTO omp_log_search.SearchJobs(PersonIdentifier, Status, RequestedUtc, CompletedUtc, HitCount, ErrorCount)
            VALUES(@identifier, 2, SYSUTCDATETIME(), SYSUTCDATETIME(), 3, 0);
            """;

        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@identifier", System.Data.SqlDbType.NVarChar, 100).Value = SensitiveIdentifier;
        await cmd.ExecuteNonQueryAsync();
    }

    private static DefaultHttpContext CreateHttpContext(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        return context;
    }

    private static async Task<string> RenderPartialAsync(
        IServiceProvider services,
        HttpContext httpContext,
        string viewPath,
        object model)
    {
        var viewEngine = services.GetRequiredService<ICompositeViewEngine>();
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath, isMainPage: false);
        Assert.True(viewResult.Success, $"View '{viewPath}' was not found.");

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = model
        };

        await using var writer = new StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewData,
            new TempDataDictionary(httpContext, services.GetRequiredService<ITempDataProvider>()),
            writer,
            new HtmlHelperOptions());
        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }
}
