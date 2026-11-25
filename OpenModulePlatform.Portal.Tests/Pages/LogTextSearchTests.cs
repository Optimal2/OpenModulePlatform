using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Pages.Admin;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Pages;

/// <summary>
/// The server-side text search of the two log pages. Each page renders a field
/// named Q with the current text, and a request with Q returns only matching
/// rows. Real routes, real compiled pages and a real database; only the login
/// is stubbed.
/// </summary>
public sealed class LogTextSearchTests : IAsyncLifetime
{
    private static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("LogTextSearch");
    private readonly string _connectionString = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(master.ConnectionString,
            $"CREATE DATABASE [{DatabaseName}];");

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        var setup = new SqlCommand("""
            IF SCHEMA_ID(N'omp') IS NULL EXEC(N'CREATE SCHEMA omp');
            IF SCHEMA_ID(N'logtextsearch') IS NULL EXEC(N'CREATE SCHEMA logtextsearch');
            CREATE TABLE omp.Modules (
                ModuleId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ModuleKey nvarchar(100) NOT NULL,
                DisplayName nvarchar(200) NOT NULL,
                SchemaName nvarchar(128) NULL,
                IsEnabled bit NOT NULL,
                SortOrder int NOT NULL
            );
            CREATE TABLE omp.users (
                user_id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                display_name nvarchar(200) NOT NULL
            );
            CREATE TABLE omp.SystemLog (
                SystemLogId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
                LoggedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
                HostName nvarchar(128) NOT NULL,
                ProcessName nvarchar(128) NOT NULL,
                Level nvarchar(16) NOT NULL,
                Logger nvarchar(256) NOT NULL,
                Message nvarchar(max) NOT NULL,
                Exception nvarchar(max) NULL,
                CorrelationId nvarchar(64) NULL
            );
            INSERT omp.Modules (ModuleKey, DisplayName, SchemaName, IsEnabled, SortOrder)
            VALUES (N'logtextsearchmod', N'Log Text Search Module', N'logtextsearch', 1, 0);
            INSERT omp.users (display_name) VALUES (N'Log search test user');
            INSERT omp.SystemLog (HostName, ProcessName, Level, Logger, Message)
            VALUES (N'testhost', N'Portal', N'Warn', N'Omp.Test.Logging', N'alpha marker reported a test warning.'),
                   (N'testhost', N'Portal', N'Error', N'Omp.Test.Logging', N'beta marker caused a test error.');
            """, conn);
        await setup.ExecuteNonQueryAsync();
        var activityLog = new SqlCommand(ActivityLogSql.CreateTableStatement("logtextsearch"), conn);
        await activityLog.ExecuteNonQueryAsync();
        var entries = new SqlCommand("""
            INSERT [logtextsearch].[ActivityLog] (LoggedUtc, OmpUserId, Entry)
            VALUES (SYSUTCDATETIME(), 1, N'{"v":1,"event":"test.signin","summary":"alpha marker signed in from the test client."}'),
                   (SYSUTCDATETIME(), NULL, N'{"v":1,"event":"test.signout","summary":"beta marker signed out of the test client."}');
            """, conn);
        await entries.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(master.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];", conn);
        try { await cmd.ExecuteNonQueryAsync(); }
        catch (SqlException ex) { OmpTestCleanupLog.RecordFailure(nameof(LogTextSearchTests), ex.Message); }
    }

    [Fact]
    public Task ActivityLogPage_RendersServerSearchFieldWithCurrentValue()
        => PageHandlerHarness.InEnglishAsync(async () =>
        {
            using var server = Server();
            using var client = server.CreateClient();
            using var response = await client.GetAsync("/admin/activitylog?Q=alpha");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("id=\"activity-qsearch-form\"", html, StringComparison.Ordinal);
            Assert.Contains("name=\"Q\"", html, StringComparison.Ordinal);
            Assert.Contains("value=\"alpha\"", html, StringComparison.Ordinal);
            Assert.Contains("data-activity-clear-q", html, StringComparison.Ordinal);
        });

    [Fact]
    public Task ActivityLogRequest_WithQ_ReturnsOnlyMatchingRows()
        => PageHandlerHarness.InEnglishAsync(async () =>
        {
            using var server = Server();
            using var client = server.CreateClient();
            using var response = await client.GetAsync("/admin/activitylog?Q=" + Uri.EscapeDataString("alpha marker"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("alpha marker signed in from the test client.", html, StringComparison.Ordinal);
            Assert.DoesNotContain("beta marker signed out", html, StringComparison.Ordinal);
        });

    [Fact]
    public Task ActivityLogClearSearch_WithPathBase_KeepsFiltersAndDropsQ()
        => PageHandlerHarness.InEnglishAsync(async () =>
        {
            using var server = Server();
            using var client = server.CreateClient();
            using var response = await client.GetAsync("/omp/admin/activitylog?Modules=logtextsearchmod&Range=all&Q=alpha");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            var href = ClearQHref(html);
            Assert.StartsWith("/omp/admin/activitylog", href, StringComparison.Ordinal);
            Assert.Contains("Modules=logtextsearchmod", href, StringComparison.Ordinal);
            Assert.Contains("Range=all", href, StringComparison.Ordinal);
            Assert.DoesNotContain("Q=", href, StringComparison.Ordinal);
        });

    [Fact]
    public Task SystemLogPage_RendersServerSearchFieldWithCurrentValue()
        => PageHandlerHarness.InEnglishAsync(async () =>
        {
            using var server = Server();
            using var client = server.CreateClient();
            using var response = await client.GetAsync("/admin/systemlog?Q=beta");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("id=\"activity-qsearch-form\"", html, StringComparison.Ordinal);
            Assert.Contains("name=\"Q\"", html, StringComparison.Ordinal);
            Assert.Contains("value=\"beta\"", html, StringComparison.Ordinal);
            Assert.Contains("data-activity-clear-q", html, StringComparison.Ordinal);
        });

    [Fact]
    public Task SystemLogRequest_WithQ_ReturnsOnlyMatchingRows()
        => PageHandlerHarness.InEnglishAsync(async () =>
        {
            using var server = Server();
            using var client = server.CreateClient();
            using var response = await client.GetAsync("/admin/systemlog?Processes=Portal&Q=" + Uri.EscapeDataString("test warning"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("alpha marker reported a test warning.", html, StringComparison.Ordinal);
            Assert.DoesNotContain("beta marker caused a test error.", html, StringComparison.Ordinal);
            // The clear-search link drops Q and keeps the other filters.
            var href = ClearQHref(html);
            Assert.Equal("/admin/systemlog?Processes=Portal", href);
        });

    [Fact]
    public Task SystemLogClearSearch_WithPathBase_KeepsFiltersAndDropsQ()
        => PageHandlerHarness.InEnglishAsync(async () =>
        {
            using var server = Server();
            using var client = server.CreateClient();
            using var response = await client.GetAsync("/omp/admin/systemlog?Processes=Portal&Levels=Warn&Q=warning");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            var href = ClearQHref(html);
            Assert.StartsWith("/omp/admin/systemlog", href, StringComparison.Ordinal);
            Assert.Contains("Processes=Portal", href, StringComparison.Ordinal);
            Assert.Contains("Levels=Warn", href, StringComparison.Ordinal);
            Assert.DoesNotContain("Q=", href, StringComparison.Ordinal);
        });

    private static string ClearQHref(string html)
    {
        var match = Regex.Match(html, "data-activity-clear-q[^>]*href=\"([^\"]+)\"|href=\"([^\"]+)\"[^>]*data-activity-clear-q");
        Assert.True(match.Success, "No clear-search link in the rendered page.");
        var href = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return System.Net.WebUtility.HtmlDecode(href);
    }

    private InMemoryPortalHost Server() => new(new HostBuilder()
        .ConfigureWebHost(web => web.UseTestServer()
        .ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddLocalization();
            services.AddHttpContextAccessor();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ForbidHandler>("test", _ => { });
            services.Configure<WebAppOptions>(o => o.AllowAnonymous = true);
            services.AddScoped<RbacService>(sp => new(null!, sp.GetRequiredService<IHttpContextAccessor>(), null!, sp.GetRequiredService<ILogger<RbacService>>()));
            services.AddSingleton(SqlConnectionFactory());
            services.AddScoped<OmpAdminRepository>();
            services.AddSingleton<ISystemLogReader>(sp => sp.GetRequiredService<OmpAdminRepository>());
            // Razor imports require these services; the page bodies never call their database methods.
            services.AddMemoryCache();
            services.AddScoped<OmpConfigurationService>(sp => new(null!, sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                sp.GetRequiredService<ILogger<OmpConfigurationService>>()));
            services.AddScoped<OmpBrandingService>();
            services.AddSingleton(new OmpTime());
            services.AddScoped<PortalUserSettingsService>(_ => new(null!));
            services.AddRazorPages(o => o.Conventions.ConfigureFilter(new RenderLogPagesFilter()))
                .AddApplicationPart(typeof(ActivityLogModel).Assembly)
                .AddApplicationPart(typeof(OpenModulePlatform.Web.Shared.ViewComponents.OmpFeedbackViewComponent).Assembly);
        })
        .Configure(app =>
        {
            app.UsePathBase("/omp");
            app.Use((context, next) =>
            {
                context.User = new(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "logtextsearch-test"), new Claim(ClaimTypes.Name, "logtextsearch-test")], "test"));
                context.Items["OMP.RbacService.RoleContext"] = new UserRoleContext
                { EffectivePermissions = new() { "OMP.Portal.Admin" } };
                return next();
            });
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapRazorPages();
            });
        })).Start());

    private SqlConnectionFactory SqlConnectionFactory()
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OmpDb"] = _connectionString })
            .Build());

    private sealed class InMemoryPortalHost(IHost host) : IDisposable
    {
        public HttpClient CreateClient() => host.GetTestClient();
        public void Dispose() => host.Dispose();
    }

    // Exercise production routing/handlers without activating the database-backed shared layout.
    private sealed class RenderLogPagesFilter : IPageFilter
    {
        public void OnPageHandlerSelected(PageHandlerSelectedContext context) { }
        public void OnPageHandlerExecuting(PageHandlerExecutingContext context) { }
        public void OnPageHandlerExecuted(PageHandlerExecutedContext context)
        {
            if (context.Result is not PageResult)
            {
                return;
            }

            var (model, path) = context.HandlerInstance switch
            {
                ActivityLogModel activity => ((PageModel)activity, "/Pages/Admin/ActivityLog.cshtml"),
                SystemLogModel system => ((PageModel)system, "/Pages/Admin/SystemLog.cshtml"),
                _ => ((PageModel?)null, (string?)null),
            };
            if (model is null || path is null)
            {
                return;
            }

            // Render the production compiled page and tag helpers without the database-backed layout/ViewStart.
            context.Result = new RenderedLogPageResult(model, path);
        }
    }

    private sealed class RenderedLogPageResult(PageModel model, string pagePath) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            var services = context.HttpContext.RequestServices;
            var engine = services.GetRequiredService<IRazorViewEngine>();
            var page = Assert.IsAssignableFrom<Page>(engine.GetPage(string.Empty, pagePath).Page);
            model.ViewData.Model = model;
            page.PageContext = model.PageContext;
            var view = new RazorView(engine, services.GetRequiredService<IRazorPageActivator>(), [], page,
                HtmlEncoder.Default, services.GetRequiredService<System.Diagnostics.DiagnosticListener>());
            using var writer = new StringWriter();
            var tempData = services.GetRequiredService<ITempDataDictionaryFactory>().GetTempData(context.HttpContext);
            await view.RenderAsync(new ViewContext(context, view, model.ViewData, tempData, writer, new HtmlHelperOptions()));
            context.HttpContext.Response.ContentType = "text/html; charset=utf-8";
            await context.HttpContext.Response.WriteAsync(writer.ToString());
        }
    }

    private sealed class ForbidHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        { Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; }
    }
}
