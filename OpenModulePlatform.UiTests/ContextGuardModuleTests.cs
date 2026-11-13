using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using OpenModulePlatform.TestSupport.Ui;
using OpenModulePlatform.Web.ContextGuardModule.Hosting;
using OpenModulePlatform.Web.ContextGuardModule.Localization;
using OpenModulePlatform.Web.ContextGuardModule.Options;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.UiTests;

[Trait("Category", "Ui")]
public sealed class ContextGuardModuleTests
{
    [Fact]
    public async Task Anonymous_cross_origin_post_logs_in_and_target_receives_only_allowed_fields()
    {
        await using var host = await ContextGuardTestHost.StartAsync();
        await using var browser = await LaunchBrowserAsync();
        await using var context = await browser.NewContextAsync();

        var page = await OpenHandoffAsync(context, host, "case-a");

        Assert.Equal(host.GuardUrl + "/show", page.Url);
        Assert.Equal("case-a", await page.GetByTestId("correlation-value").TextContentAsync());
        var frame = page.FrameLocator("iframe[name='context-target']");
        Assert.Equal("reference-a", await frame.GetByTestId("received-contextReference").TextContentAsync());
        Assert.Equal("case-a", await frame.GetByTestId("received-correlation").TextContentAsync());
        Assert.Equal(0, await frame.GetByTestId("received-dropped").CountAsync());
    }

    [Fact]
    public async Task Second_handoff_for_same_user_supersedes_and_blanks_first_tab_within_two_intervals()
    {
        await using var host = await ContextGuardTestHost.StartAsync();
        await using var browser = await LaunchBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var first = await OpenHandoffAsync(context, host, "case-b-first");
        var heartbeatResponses = new List<IResponse>();
        first.Response += (_, response) =>
        {
            if (response.Url.Contains("/context/", StringComparison.Ordinal)
                && response.Url.Contains("/heartbeat", StringComparison.Ordinal))
            {
                heartbeatResponses.Add(response);
            }
        };

        var second = await OpenHandoffAsync(context, host, "case-b-second");

        await first.Locator("#context-status[data-outcome='superseded']").WaitForAsync(new() { Timeout = 5000 });
        Assert.Equal(0, await first.Locator("iframe[name='context-target']").CountAsync());
        Assert.Equal(1, await second.Locator("iframe[name='context-target']").CountAsync());
        Assert.Contains(heartbeatResponses, response => response.TextAsync().GetAwaiter().GetResult()
            .Contains("superseded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task User_and_client_scope_does_not_supersede_another_browser_client()
    {
        await using var host = await ContextGuardTestHost.StartAsync(ContextGuardScopes.UserAndClient);
        await using var browser = await LaunchBrowserAsync();
        await using var firstContext = await browser.NewContextAsync();
        await using var secondContext = await browser.NewContextAsync();
        var first = await OpenHandoffAsync(firstContext, host, "case-c-first");

        var second = await OpenHandoffAsync(secondContext, host, "case-c-second");

        await Task.Delay(1200);
        Assert.Equal(1, await first.Locator("iframe[name='context-target']").CountAsync());
        Assert.Equal(1, await second.Locator("iframe[name='context-target']").CountAsync());
        var contextId = await first.Locator("#context-guard").GetAttributeAsync("data-context-id");
        var state = await firstContext.APIRequest.GetAsync($"{host.GuardUrl}/context/{contextId}/state");
        Assert.Equal(200, state.Status);
        Assert.Contains("current", await state.TextAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redeemed_and_expired_keys_show_named_handoff_pages()
    {
        await using var host = await ContextGuardTestHost.StartAsync();
        await using var browser = await LaunchBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenHandoffAsync(context, host, "case-d-first");

        var usedKey = await CreateHandoffKeyAsync(context, host, "case-d-redeemed");
        await page.GotoAsync(host.AppAddress + usedKey);
        await page.GotoAsync(host.AppAddress + usedKey);
        await page.Locator("[data-testid='handoff-error'][data-outcome='AlreadyRedeemed']").WaitForAsync();

        await using var expiredHost = await ContextGuardTestHost.StartAsync(handoffTtlSeconds: 1);
        await context.APIRequest.GetAsync(expiredHost.GuardUrl + "/auth/login?returnUrl=%2Fguard%2Fshow");
        var expiredKey = await CreateHandoffKeyAsync(context, expiredHost, "case-d-expired");
        await Task.Delay(1100);
        await page.GotoAsync(expiredHost.AppAddress + expiredKey);
        await page.Locator("[data-testid='handoff-error'][data-outcome='Expired']").WaitForAsync();
    }

    [Fact]
    public async Task End_call_from_calling_system_blanks_the_guard_tab()
    {
        await using var host = await ContextGuardTestHost.StartAsync();
        await using var browser = await LaunchBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenHandoffAsync(context, host, "case-e");

        var response = await context.APIRequest.PostAsync(host.GuardUrl + "/handoff/demo/end", new()
        {
            DataString = "correlation=case-e",
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" }
        });

        Assert.Equal(204, response.Status);
        await page.Locator("#context-status[data-outcome='ended']").WaitForAsync(new() { Timeout = 5000 });
        Assert.Equal(0, await page.Locator("iframe[name='context-target']").CountAsync());
    }

    private static async Task<IPage> OpenHandoffAsync(
        IBrowserContext context,
        ContextGuardTestHost host,
        string correlation)
    {
        var page = await context.NewPageAsync();
        var caller = host.TargetUrl + "/calling-system"
            + "?action=" + Uri.EscapeDataString(host.GuardUrl + "/handoff/demo")
            + "&correlation=" + Uri.EscapeDataString(correlation);
        await page.GotoAsync(caller);
        await page.GetByRole(AriaRole.Button, new() { Name = "Open context" }).ClickAsync();
        try
        {
            await page.WaitForURLAsync(host.GuardUrl + "/show", new() { Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            Console.WriteLine("Context Guard navigation ended at " + page.Url);
            Console.WriteLine(await page.ContentAsync());
            throw;
        }
        await page.FrameLocator("iframe[name='context-target']")
            .GetByTestId("received-correlation").WaitForAsync(new() { Timeout = 15_000 });
        return page;
    }

    private static async Task<string> CreateHandoffKeyAsync(
        IBrowserContext context,
        ContextGuardTestHost host,
        string correlation)
    {
        var response = await context.APIRequest.PostAsync(host.GuardUrl + "/handoff/demo", new()
        {
            DataString = "correlation=" + Uri.EscapeDataString(correlation),
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded" },
            MaxRedirects = 0
        });
        Assert.Equal(303, response.Status);
        return response.Headers["location"];
    }

    private static async Task<IBrowser> LaunchBrowserAsync()
    {
        var playwright = await Playwright.CreateAsync();
        try
        {
            return await playwright.Chromium.LaunchAsync(new() { Headless = true });
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    private sealed class ContextGuardTestHost : IAsyncDisposable
    {
        private ContextGuardTestHost(WebApplication target, WebApplication guard, string appAddress)
        {
            Target = target;
            Guard = guard;
            AppAddress = appAddress;
            TargetUrl = Address(target);
            GuardUrl = AppAddress + "/guard";
        }

        public WebApplication Target { get; }
        public WebApplication Guard { get; }
        public string AppAddress { get; }
        public string TargetUrl { get; }
        public string GuardUrl { get; }

        public static async Task<ContextGuardTestHost> StartAsync(
            string scope = ContextGuardScopes.User,
            int handoffTtlSeconds = 60)
        {
            PrepareStaticAssets();
            var targetBuilder = WebApplication.CreateBuilder();
            targetBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            var target = targetBuilder.Build();
            MapTarget(target);
            await target.StartAsync();
            try
            {
                var guardBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    ApplicationName = typeof(ContextGuardModuleResource).Assembly.GetName().Name,
                    EnvironmentName = "Production"
                });
                guardBuilder.WebHost.UseStaticWebAssets();
                guardBuilder.WebHost.UseUrls("http://127.0.0.1:0");
                var targetUrl = Address(target);
                guardBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Portal:AllowAnonymous"] = "false",
                    ["Portal:DefaultCulture"] = "en-US",
                    ["Portal:SupportedCultures:0"] = "en-US",
                    ["Portal:PortalTopBar:Enabled"] = "false",
                    ["OmpAuth:CookieSecurePolicy"] = "sameAsRequest",
                    ["OmpAuth:LoginPath"] = "/guard/auth/login",
                    ["ContextGuard:Scope"] = scope,
                    ["ContextGuard:HandoffTtlSeconds"] = handoffTtlSeconds.ToString(),
                    ["ContextGuard:HeartbeatIntervalSeconds"] = "1",
                    ["ContextGuard:HeartbeatTimeoutSeconds"] = "10",
                    ["ContextGuard:HeartbeatFailureLimit"] = "2",
                    ["ContextGuard:Profiles:demo:DisplayName"] = "Test calling profile",
                    ["ContextGuard:Profiles:demo:TargetUrl"] = targetUrl + "/target",
                    ["ContextGuard:Profiles:demo:Method"] = "POST",
                    ["ContextGuard:Profiles:demo:Delivery"] = "embed",
                    ["ContextGuard:Profiles:demo:AllowedFieldNames:0"] = "correlation",
                    ["ContextGuard:Profiles:demo:AllowedFieldNames:1"] = "contextReference",
                    ["ContextGuard:Profiles:demo:CorrelationFieldName"] = "correlation"
                });
                guardBuilder.AddOmpWebDefaults<ContextGuardModuleResource>(optionsSectionName: "Portal");
                guardBuilder.Services.AddRazorPages()
                    .AddApplicationPart(typeof(ContextGuardModuleResource).Assembly);
                guardBuilder.Services.AddContextGuardModule(guardBuilder.Configuration);
                guardBuilder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
                guardBuilder.Services.PostConfigure<CookieAuthenticationOptions>(
                    OmpAuthDefaults.AuthenticationScheme,
                    options => options.Events.OnValidatePrincipal = _ => Task.CompletedTask);

                var guard = guardBuilder.Build();
                guard.UsePathBase("/guard");
                // Pin routing after PathBase; WebApplication's automatic routing would
                // otherwise run before this test-only PathBase middleware.
                guard.UseRouting();
                guard.MapGet("/auth/login", async (HttpContext context, string? returnUrl) =>
                {
                    var identity = new ClaimsIdentity(
                        [new Claim(OmpAuthDefaults.UserIdClaimType, "42")],
                        OmpAuthDefaults.AuthenticationScheme);
                    await context.SignInAsync(
                        OmpAuthDefaults.AuthenticationScheme,
                        new ClaimsPrincipal(identity));
                    var safeReturn = !string.IsNullOrWhiteSpace(returnUrl)
                        && returnUrl.StartsWith('/')
                        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
                        ? returnUrl
                        : "/";
                    return Results.Redirect(safeReturn);
                }).AllowAnonymous();
                guard.UseContextGuardCsp();
                guard.MapContextGuardEndpoints();
                guard.UseOmpWebDefaults(optionsSectionName: "Portal", mapRazorPages: true);
                await guard.StartAsync();
                return new ContextGuardTestHost(target, guard, Address(guard));
            }
            catch
            {
                await target.StopAsync();
                await target.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Guard.StopAsync();
            await Guard.DisposeAsync();
            await Target.StopAsync();
            await Target.DisposeAsync();
        }

        private static void PrepareStaticAssets()
        {
            // UiTests deliberately sets StaticWebAssetsEnabled=false. Its normal in-process
            // pages need no static files; this real module page does, so copy the small asset
            // set into the fixture's output instead of changing the shared project contract.
            var repoRoot = UiTestPaths.FindRepoRoot("OpenModulePlatform.slnx");
            CopyTree(
                Path.Join(repoRoot, "OpenModulePlatform.Web.ContextGuardModule", "wwwroot"),
                Path.Join(AppContext.BaseDirectory, "wwwroot"));
            CopyTree(
                Path.Join(repoRoot, "OpenModulePlatform.Web.Shared", "wwwroot"),
                Path.Join(AppContext.BaseDirectory, "wwwroot", "_content", "OpenModulePlatform.Web.Shared"));
        }

        private static void CopyTree(string source, string destination)
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Join(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static string Address(WebApplication app)
            => app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');

        private static void MapTarget(WebApplication app)
        {
            app.MapGet("/calling-system", (HttpContext context, string action, string correlation) =>
            {
                var html = "<!doctype html><html><body><form method='post' action='"
                    + HtmlEncoder.Default.Encode(action) + "'>"
                    + "<input type='hidden' name='correlation' value='"
                    + HtmlEncoder.Default.Encode(correlation) + "'>"
                    + "<input type='hidden' name='contextReference' value='reference-a'>"
                    + "<input type='hidden' name='dropped' value='must-not-arrive'>"
                    + "<button type='submit'>Open context</button></form></body></html>";
                return Results.Content(html, "text/html", Encoding.UTF8);
            });
            app.MapPost("/target", async (HttpContext context) =>
            {
                var form = await context.Request.ReadFormAsync();
                return TargetFields(form.SelectMany(pair => pair.Value.Select(value =>
                    new KeyValuePair<string, string>(pair.Key, value ?? string.Empty))));
            });
            app.MapGet("/target", (HttpContext context) => TargetFields(
                context.Request.Query.SelectMany(pair => pair.Value.Select(value =>
                    new KeyValuePair<string, string>(pair.Key, value ?? string.Empty)))));
        }

        private static IResult TargetFields(IEnumerable<KeyValuePair<string, string>> fields)
        {
            var html = new StringBuilder("<!doctype html><html><body><h1>Test target</h1>");
            foreach (var (name, value) in fields)
            {
                html.Append("<div data-testid='received-")
                    .Append(HtmlEncoder.Default.Encode(name)).Append("'>")
                    .Append(HtmlEncoder.Default.Encode(value)).Append("</div>");
            }
            html.Append("</body></html>");
            return Results.Content(html.ToString(), "text/html", Encoding.UTF8);
        }
    }
}
