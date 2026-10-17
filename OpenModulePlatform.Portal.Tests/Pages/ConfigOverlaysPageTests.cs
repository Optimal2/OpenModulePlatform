using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
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

    private static InMemoryPortalHost Server(OverlayAdminFakeStore store, bool admin = true) => new(new HostBuilder()
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
            services.AddRazorPages(o => o.Conventions.ConfigureFilter(new ReadbackFilter()))
                .AddApplicationPart(typeof(ConfigOverlaysModel).Assembly);
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
    private sealed class ReadbackFilter : IPageFilter
    {
        public void OnPageHandlerSelected(PageHandlerSelectedContext context) { }
        public void OnPageHandlerExecuting(PageHandlerExecutingContext context) { }
        public void OnPageHandlerExecuted(PageHandlerExecutedContext context)
        {
            if (context.Result is PageResult && context.HandlerInstance is ConfigOverlaysModel model)
                context.Result = new JsonResult(new { model.Draft, Errors = model.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage) });
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
