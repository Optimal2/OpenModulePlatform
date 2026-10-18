using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Pages.Admin;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Portal.Tests.Services;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.Text.Encodings.Web;

namespace OpenModulePlatform.Portal.Tests.Pages;

/// <summary>In-memory MVC routing, real PageModel handlers and antiforgery. No app startup, SQL or sockets.</summary>
public sealed class ConfigOverlaysPageTests
{
    [Fact]
    public async Task ActualPageRouteInvokesAuthorizedHandlerAndMissingDocumentIs404()
    {
        var store = new OverlayAdminFakeStore();
        using var server = Server(store);
        using var client = server.CreateClient();
        var response = await client.GetAsync("/admin/configoverlays");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, store.Reads);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/admin/configoverlays?id=999")).StatusCode);
    }

    [Theory]
    [InlineData("host-a")]
    [InlineData("HOST-A")]
    [InlineData("HoSt-A")]
    public async Task HostQueryPrefillsRegisteredKeyRegardlessOfCase(string hostKey)
    {
        var store = new OverlayAdminFakeStore();
        using var server = Server(store);
        using var client = server.CreateClient();
        Assert.Equal("host-a", await PrefilledHost(client, hostKey));
        Assert.Equal(1, store.Reads);
        Assert.Equal(0, store.SourceReads);
        Assert.Empty(store.DetailReads);
        Assert.Null(store.Saved);
        Assert.Empty(store.Changes);
    }

    [Theory]
    [InlineData("unknown-host")]
    [InlineData("*")]
    [InlineData("")]
    public async Task HostQueryDoesNotPrefillUnregisteredKeys(string hostKey)
    {
        using var server = Server(new OverlayAdminFakeStore());
        using var client = server.CreateClient();
        Assert.Null(await PrefilledHost(client, hostKey));
    }

    [Fact]
    public async Task HostQueryCannotPrefillHostRegisteredOnlyInAnotherInstallation()
    {
        using var localServer = Server(new OverlayAdminFakeStore());
        using var foreignServer = Server(new OverlayAdminFakeStore
        {
            Snapshot = ConfigOverlayAdministrationTests.Snapshot() with
            { Hosts = [new() { HostKey = "other-host" }] }
        });
        using var localClient = localServer.CreateClient();
        using var foreignClient = foreignServer.CreateClient();
        Assert.Equal("other-host", await PrefilledHost(foreignClient, "OTHER-HOST"));
        Assert.Null(await PrefilledHost(localClient, "OTHER-HOST"));
    }

    [Fact]
    public async Task HostQueryDoesNotPrefillAmbiguousHostIdentity()
    {
        using var server = Server(new OverlayAdminFakeStore
        {
            Snapshot = ConfigOverlayAdministrationTests.Snapshot() with
            { Hosts = [new() { HostKey = "host-a" }, new() { HostKey = "HOST-A" }] }
        });
        using var client = server.CreateClient();
        Assert.Null(await PrefilledHost(client, "host-a"));
    }

    [Theory]
    [InlineData("host-a", false, "host-a", 1)]
    [InlineData("HOST-A", false, "host-a", 1)]
    [InlineData("HoSt-A", false, "host-a", 1)]
    [InlineData("unknown-host", false, "", 0)]
    [InlineData("other-installation-host", false, "", 0)]
    [InlineData("host-a", true, "", 0)]
    public async Task RenderedReadbackSelectsOnlyUniqueRegisteredIdentity(string query, bool ambiguous,
        string selectedHost, int sourceReads)
    {
        var store = new OverlayAdminFakeStore();
        if (ambiguous)
            store.Snapshot = store.Snapshot with { Hosts = [new() { HostKey = "host-a" }, new() { HostKey = "HOST-A" }] };
        using var server = Server(store, renderPage: true);
        using var client = server.CreateClient();
        using var response = await client.GetAsync($"/admin/configoverlays?HostKey={Uri.EscapeDataString(query)}&ArtifactId=42");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var select = Regex.Match(html, "<select[^>]*id=\"HostKey\"[^>]*>(.*?)</select>", RegexOptions.Singleline);
        Assert.True(select.Success, html);
        var options = Regex.Matches(select.Groups[1].Value, "<option[^>]*>");
        var explicitSelections = options.Where(o => o.Value.Contains("selected=\"selected\"", StringComparison.Ordinal)).ToArray();
        // A single-select with no explicit selection displays its first option in the browser.
        var selected = explicitSelections.Length == 0 ? options[0] : Assert.Single(explicitSelections);
        Assert.Contains($"value=\"{selectedHost}\"", selected.Value);
        Assert.Equal(sourceReads, store.SourceReads);
        if (sourceReads == 0)
            Assert.Contains("Select a registered host and artifact in this installation.", html);
    }

    private static async Task<string?> PrefilledHost(HttpClient client, string hostKey)
    {
        using var response = await client.GetAsync($"/admin/configoverlays?HostKey={Uri.EscapeDataString(hostKey)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var draftHost = body.RootElement.GetProperty("draft").GetProperty("hostKey").GetString();
        Assert.Equal(draftHost, body.RootElement.GetProperty("hostKey").GetString());
        return draftHost;
    }

    [Theory]
    [InlineData("Save")]
    [InlineData("Enable")]
    [InlineData("Disable")]
    [InlineData("Delete")]
    public async Task EveryMutationRequiresRealRazorAntiforgery(string handler)
    {
        var store = new OverlayAdminFakeStore();
        using var server = Server(store);
        using var client = server.CreateClient();
        var response = await client.PostAsync($"/admin/configoverlays?handler={handler}", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, store.Reads);
        Assert.Empty(store.Changes);
        Assert.Null(store.Saved);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("Save")]
    [InlineData("Enable")]
    [InlineData("Disable")]
    [InlineData("Delete")]
    public async Task EveryHandlerDeniesNonAdminBeforeStoreReads(string handler)
    {
        var store = new OverlayAdminFakeStore();
        using var server = Server(store, admin: false);
        using var client = server.CreateClient();
        HttpResponseMessage response;
        if (handler == "GET") response = await client.GetAsync("/admin/configoverlays?HostKey=host-a&ArtifactId=42&id=1");
        else
        {
            var form = await TokenForm(client);
            response = await client.PostAsync($"/admin/configoverlays?handler={handler}", new FormUrlEncodedContent(form));
        }
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, store.Reads);
        Assert.Empty(store.DetailReads);
    }

    [Fact]
    public async Task AuthorizedSavePersistsOneAllHostsDocumentAndRedirectsToDetail()
    {
        var store = new OverlayAdminFakeStore();
        using var server = Server(store);
        using var client = server.CreateClient();
        var form = await TokenForm(client);
        form["Draft.Scope"] = "AllHosts";
        form["Draft.Json"] = """{"overlayKey":"settings","overlayVersion":"1","moduleDefinitionVersion":"3.4.5","configurationFiles":[]}""";
        var response = await client.PostAsync("/admin/configoverlays?handler=Save", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("id=1", response.Headers.Location!.ToString());
        Assert.Equal("*", store.Saved!.HostKey);
        Assert.Equal("3.4.5", store.Saved.ModuleDefinitionVersion);
    }

    [Theory]
    [InlineData("Enable", true)]
    [InlineData("Disable", false)]
    [InlineData("Delete", null)]
    public async Task AuthorizedActionsUseDocumentIdentityAndConcurrencyToken(string handler, bool? enabled)
    {
        var row = ConfigOverlayAdministrationTests.Overlay();
        var store = new OverlayAdminFakeStore { Snapshot = ConfigOverlayAdministrationTests.Snapshot(row) };
        using var server = Server(store);
        using var client = server.CreateClient();
        var form = await TokenForm(client);
        form["id"] = "1"; form["token"] = ConfigOverlaysModel.Token(row);
        var response = await client.PostAsync($"/admin/configoverlays?handler={handler}", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal((1, ConfigOverlaysModel.Token(row), enabled), Assert.Single(store.Changes));
    }

    [Fact]
    public async Task SchemaNotReadyPreventsMutationAndForeignIdIsNotFound()
    {
        var store = new OverlayAdminFakeStore
        { Snapshot = ConfigOverlayAdministrationTests.Snapshot(ConfigOverlayAdministrationTests.Overlay()) with { SchemaReady = false } };
        using var server = Server(store);
        using var client = server.CreateClient();
        var form = await TokenForm(client);
        form["id"] = "1"; form["token"] = "token";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/admin/configoverlays?handler=Enable", new FormUrlEncodedContent(form))).StatusCode);
        form["id"] = "999";
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/admin/configoverlays?handler=Delete", new FormUrlEncodedContent(form))).StatusCode);
        Assert.Empty(store.Changes);
    }

    [Fact]
    public async Task RejectedSaveKeepsDraftAndDoesNotExposeExceptionOrReadEffectiveSources()
    {
        var store = new OverlayAdminFakeStore { RejectSave = true };
        using var server = Server(store);
        using var client = server.CreateClient();
        var form = await TokenForm(client);
        form["Draft.Scope"] = "Host"; form["Draft.HostKey"] = "host-a";
        form["Draft.Json"] = """{"overlayKey":"draft-marker","overlayVersion":"1"}""";
        var response = await client.PostAsync("/admin/configoverlays?handler=Save", new FormUrlEncodedContent(form));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("draft-marker", body);
        Assert.DoesNotContain("private-secret", body);
        Assert.Equal(0, store.SourceReads);
    }

    [Fact]
    public async Task PreviewGetDeniesNonAdminBeforeStoreReads()
    {
        var store = new OverlayAdminFakeStore
        { Snapshot = ConfigOverlayAdministrationTests.Snapshot(ConfigOverlayAdministrationTests.Overlay(1)) };
        using var server = Server(store, admin: false);
        using var client = server.CreateClient();
        var response = await client.GetAsync("/admin/configoverlays?id=1&HostKey=host-a&ArtifactId=42");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, store.Reads);
        Assert.Empty(store.DetailReads);
    }

    [Fact]
    public async Task RenderedPreviewShowsFallbackWinnerAndPreviewTokenWithoutContent()
    {
        var host = ConfigOverlayAdministrationTests.Overlay(1);
        var allHosts = ConfigOverlayAdministrationTests.Overlay(2, "AllHosts", version: "9.0.0");
        var store = new OverlayAdminFakeStore { Snapshot = ConfigOverlayAdministrationTests.Snapshot(host, allHosts) };
        store.Details[1] = new(host, "", [ConfigOverlayAdministrationTests.File(1, 1, """{"host":true}""")]);
        store.Details[2] = new(allHosts, "", [ConfigOverlayAdministrationTests.File(2, 2, """{"shared":true}""")]);
        using var server = Server(store, renderPage: true);
        using var client = server.CreateClient();
        using var response = await client.GetAsync("/admin/configoverlays?id=1&HostKey=host-a&ArtifactId=42");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Removal impact preview", html);
        Assert.Contains("FallsBack", html);
        Assert.Contains("overlay-2", html);
        Assert.Contains(ConfigOverlaysModel.Token(host), html);
        Assert.DoesNotContain("\"host\":true", html);
        Assert.DoesNotContain("{\"shared\":true}", html);
    }

    [Fact]
    public async Task PreviewTokenMatchesActionTokenForSameRow()
    {
        var row = ConfigOverlayAdministrationTests.Overlay(1);
        var store = new OverlayAdminFakeStore { Snapshot = ConfigOverlayAdministrationTests.Snapshot(row) };
        store.Details[1] = new(row, "", [ConfigOverlayAdministrationTests.File(1, 1)]);
        var preview = await new ConfigOverlayAdministration(store).PreviewRemovalAsync(store.Snapshot, 1, "host-a", 42, default);
        Assert.Equal(ConfigOverlaysModel.Token(row),
            OmpAdminRepository.OverlayEditToken(preview.OverlaySha256, preview.UpdatedUtc));
    }

    [Fact]
    public void CompiledPageAndNavigationKeepDraftAndReadbackSeparate()
    {
        var markup = OmpRepositoryFiles.ReadRepositoryTextFile("OpenModulePlatform.Portal", "Pages", "Admin", "ConfigOverlays.cshtml");
        Assert.Contains("@page \"/admin/configoverlays\"", markup);
        Assert.Contains("asp-page-handler=\"Save\"", markup);
        Assert.Contains("<form method=\"get\">", markup);
        Assert.Contains("asp-for=\"Draft.Json\"", markup);
        Assert.DoesNotContain("Html.Raw", markup);
        foreach (var page in new[] { "Hosts", "InstanceTemplateEdit", "ModulePackageImport" })
            Assert.Contains("~/admin/configoverlays", OmpRepositoryFiles.ReadRepositoryTextFile("OpenModulePlatform.Portal", "Pages", "Admin", page + ".cshtml"));
    }

    private static async Task<Dictionary<string, string>> TokenForm(HttpClient client)
    {
        var response = await client.GetAsync("/test-token");
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        return new() { ["__RequestVerificationToken"] = await response.Content.ReadAsStringAsync() };
    }

    private static InMemoryPortalHost Server(OverlayAdminFakeStore store, bool admin = true, bool renderPage = false) => new(new HostBuilder()
        .ConfigureWebHost(web => web.UseTestServer()
        .ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddLocalization();
            services.AddHttpContextAccessor();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, ForbidHandler>("test", _ => { });
            services.Configure<WebAppOptions>(o => o.AllowAnonymous = false);
            services.AddScoped<RbacService>(sp => new(null!, sp.GetRequiredService<IHttpContextAccessor>(), null!, sp.GetRequiredService<ILogger<RbacService>>()));
            services.AddSingleton<IConfigOverlayAdminStore>(store);
            services.AddScoped<ConfigOverlayAdministration>();
            // Razor imports require these services; the page body never calls their database methods.
            services.AddMemoryCache();
            services.AddScoped<OmpConfigurationService>(sp => new(null!, sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                sp.GetRequiredService<ILogger<OmpConfigurationService>>()));
            services.AddScoped<OmpBrandingService>();
            services.AddSingleton(new OmpTime());
            services.AddScoped<PortalUserSettingsService>(_ => new(null!));
            services.AddRazorPages(o => o.Conventions.ConfigureFilter(new ReadbackFilter(renderPage)))
                .AddApplicationPart(typeof(ConfigOverlaysModel).Assembly)
                .AddApplicationPart(typeof(OpenModulePlatform.Web.Shared.ViewComponents.OmpFeedbackViewComponent).Assembly);
        })
        .Configure(app =>
        {
            app.Use((context, next) =>
            {
                context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "overlay-admin-test")], "test"));
                context.Items["OMP.RbacService.RoleContext"] = new UserRoleContext
                { EffectivePermissions = admin ? new() { "OMP.Portal.Admin" } : new() };
                return next();
            });
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/test-token", async context =>
                    await context.Response.WriteAsync(context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context).RequestToken!));
                endpoints.MapRazorPages();
            });
        })).Start());

    private sealed class InMemoryPortalHost(IHost host) : IDisposable
    {
        public HttpClient CreateClient() => host.GetTestClient();
        public void Dispose() => host.Dispose();
    }

    // Exercise production routing/handlers without activating the database-backed shared layout.
    private sealed class ReadbackFilter(bool renderPage) : IPageFilter
    {
        public void OnPageHandlerSelected(PageHandlerSelectedContext context) { }
        public void OnPageHandlerExecuting(PageHandlerExecutingContext context) { }
        public void OnPageHandlerExecuted(PageHandlerExecutedContext context)
        {
            if (context.Result is PageResult && context.HandlerInstance is ConfigOverlaysModel model)
            {
                if (renderPage)
                {
                    // Render the production compiled page and tag helpers without the database-backed layout/ViewStart.
                    context.Result = new RenderedPageResult(model);
                }
                else
                    context.Result = new JsonResult(new { model.Draft, model.HostKey, Errors = model.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage) });
            }
        }
    }

    private sealed class RenderedPageResult(ConfigOverlaysModel model) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            var services = context.HttpContext.RequestServices;
            var engine = services.GetRequiredService<IRazorViewEngine>();
            var page = Assert.IsAssignableFrom<Page>(engine.GetPage(string.Empty, "/Pages/Admin/ConfigOverlays.cshtml").Page);
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
