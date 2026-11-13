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
using OpenModulePlatform.Web.ContextGuardModule.Pages;
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

        var (redeemedPath, redeemedKey) = await ParkHandoffKeyAsync(context, host, "case-d-redeemed");
        await page.GotoAsync(host.AppAddress + redeemedPath);
        // The first visit consumes the parked cookie, so park the same key
        // again: a genuine reuse must report AlreadyRedeemed, not Unknown.
        await context.AddCookiesAsync([new Cookie
        {
            Name = ShowModel.HandoffKeyCookieName,
            Value = redeemedKey,
            Domain = new Uri(host.AppAddress).Host,
            Path = "/guard/show"
        }]);
        await page.GotoAsync(host.AppAddress + redeemedPath);
        await page.Locator("[data-testid='handoff-error'][data-outcome='AlreadyRedeemed']").WaitForAsync();

        await using var expiredHost = await ContextGuardTestHost.StartAsync(handoffTtlSeconds: 1);
        await context.APIRequest.GetAsync(expiredHost.GuardUrl + "/auth/login?returnUrl=%2Fguard%2Fshow");
        var (expiredPath, _) = await ParkHandoffKeyAsync(context, expiredHost, "case-d-expired");
        await Task.Delay(1100);
        await page.GotoAsync(expiredHost.AppAddress + expiredPath);
        await page.Locator("[data-testid='handoff-error'][data-outcome='Expired']").WaitForAsync();
    }

    [Fact]
    public async Task Handoff_key_never_appears_in_browser_urls()
    {
        await using var host = await ContextGuardTestHost.StartAsync();
        await using var browser = await LaunchBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var seenUrls = new List<string>();

        var page = await OpenHandoffAsync(context, host, "case-url-check", seenUrls);

        Assert.DoesNotContain("k=", page.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(seenUrls, url => url.Contains("k=", StringComparison.Ordinal));
        Assert.Equal("case-url-check", await page.GetByTestId("correlation-value").TextContentAsync());
    }

    [Fact]
    public async Task Field_value_with_markup_and_script_is_rendered_inert()
    {
        await using var host = await ContextGuardTestHost.StartAsync();
        await using var browser = await LaunchBrowserAsync();
        await using var context = await browser.NewContextAsync();
        const string payload = """</strong><script>window.__pwned=true</script><img src="x" onerror="window.__pwned=true">""";
        var dialogSeen = false;

        var page = await OpenHandoffAsync(context, host, payload, onPage: p =>
        {
            p.Dialog += (_, _) => { dialogSeen = true; };
        });

        Assert.Equal(payload, await page.GetByTestId("correlation-value").TextContentAsync());
        Assert.False(await page.EvaluateAsync<bool>("window.__pwned === true"));
        Assert.False(dialogSeen);
        var frame = page.FrameLocator("iframe[name='context-target']");
        Assert.Equal(payload, await frame.GetByTestId("received-correlation").TextContentAsync());
        var targetFrame = page.Frame(name: "context-target");
        Assert.NotNull(targetFrame);
        Assert.False(await targetFrame.EvaluateAsync<bool>("window.__pwned === true"));
    }

    [Fact]
    public async Task Heartbeat_without_or_with_invalid_antiforgery_token_is_refused()
    {
        await using var host = await ContextGuardTestHost.StartAsync();
        await using var browser = await LaunchBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var page = await OpenHandoffAsync(context, host, "case-token-check");

        var contextId = await page.Locator("#context-guard").GetAttributeAsync("data-context-id");
        Assert.False(string.IsNullOrWhiteSpace(contextId));
        var missing = await context.APIRequest.PostAsync($"{host.GuardUrl}/context/{contextId}/heartbeat");
        Assert.Equal(400, missing.Status);
        var bogus = await context.APIRequest.PostAsync($"{host.GuardUrl}/context/{contextId}/heartbeat", new()
        {
            Headers = new Dictionary<string, string> { ["RequestVerificationToken"] = "bogus-token" }
        });
        Assert.Equal(400, bogus.Status);
    }

    [Fact]
    public async Task End_with_short_correlation_is_refused()
    {
        await using var host = await ContextGuardTestHost.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        var response = await client.PostAsync(
            host.GuardUrl + "/handoff/strict/end",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["correlation"] = "short" }));

        Assert.Equal(400, (int)response.StatusCode);
    }

    [Fact]
    public async Task End_with_disabled_profile_returns_not_found()
    {
        await using var host = await ContextGuardTestHost.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        var response = await client.PostAsync(
            host.GuardUrl + "/handoff/closed/end",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["correlation"] = "long-correlation-value-123" }));

        Assert.Equal(404, (int)response.StatusCode);
    }

    [Theory]
    [InlineData(null, 403)]
    [InlineData("wrong-secret", 403)]
    [InlineData("test-end-secret", 204)]
    public async Task End_with_shared_secret_requires_exact_match(string? secret, int expectedStatus)
    {
        await using var host = await ContextGuardTestHost.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var fields = new Dictionary<string, string> { ["correlation"] = "long-correlation-value-123" };
        if (secret is not null)
        {
            fields["endSecret"] = secret;
        }

        var response = await client.PostAsync(
            host.GuardUrl + "/handoff/secret/end",
            new FormUrlEncodedContent(fields));

        Assert.Equal(expectedStatus, (int)response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_endpoints_are_rate_limited_per_remote_address()
    {
        await using var host = await ContextGuardTestHost.StartAsync(receiveRateLimit: 2, endRateLimit: 2);
        await using var browser = await LaunchBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var formHeaders = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/x-www-form-urlencoded"
        };

        var firstReceive = await context.APIRequest.PostAsync(host.GuardUrl + "/handoff/demo", new()
        {
            DataString = "correlation=case-rl-1",
            Headers = formHeaders,
            MaxRedirects = 0
        });
        var secondReceive = await context.APIRequest.PostAsync(host.GuardUrl + "/handoff/demo", new()
        {
            DataString = "correlation=case-rl-2",
            Headers = formHeaders,
            MaxRedirects = 0
        });
        var thirdReceive = await context.APIRequest.PostAsync(host.GuardUrl + "/handoff/demo", new()
        {
            DataString = "correlation=case-rl-3",
            Headers = formHeaders,
            MaxRedirects = 0
        });

        Assert.Equal(303, firstReceive.Status);
        Assert.Equal(303, secondReceive.Status);
        Assert.Equal(429, thirdReceive.Status);

        var firstEnd = await context.APIRequest.PostAsync(host.GuardUrl + "/handoff/demo/end", new()
        {
            DataString = "correlation=case-rl-9",
            Headers = formHeaders
        });
        var secondEnd = await context.APIRequest.PostAsync(host.GuardUrl + "/handoff/demo/end", new()
        {
            DataString = "correlation=case-rl-9",
            Headers = formHeaders
        });
        var thirdEnd = await context.APIRequest.PostAsync(host.GuardUrl + "/handoff/demo/end", new()
        {
            DataString = "correlation=case-rl-9",
            Headers = formHeaders
        });

        Assert.Equal(204, firstEnd.Status);
        Assert.Equal(204, secondEnd.Status);
        Assert.Equal(429, thirdEnd.Status);
    }

    [Theory]
    [InlineData(null, 403)]
    [InlineData("https://evil.example/path", 403)]
    public async Task Handoff_with_disallowed_or_missing_origin_is_refused(string? origin, int expectedStatus)
    {
        await using var host = await ContextGuardTestHost.StartAsync();
        await using var browser = await LaunchBrowserAsync();
        await using var context = await browser.NewContextAsync();
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/x-www-form-urlencoded"
        };
        if (origin is not null)
        {
            headers["Origin"] = origin;
        }

        var response = await context.APIRequest.PostAsync(host.GuardUrl + "/handoff/locked", new()
        {
            DataString = "correlation=case-origin-check",
            Headers = headers
        });

        Assert.Equal(expectedStatus, response.Status);
    }

    [Fact]
    public async Task Oversized_body_field_count_and_value_limits_are_refused()
    {
        await using var host = await ContextGuardTestHost.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        var bigBody = await client.PostAsync(
            host.GuardUrl + "/handoff/demo",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["correlation"] = "case-limit-body",
                ["filler"] = new string('x', 70_000)
            }));
        Assert.Equal(413, (int)bigBody.StatusCode);

        var manyFields = new Dictionary<string, string> { ["correlation"] = "case-limit-count" };
        for (var i = 0; i < 70; i++)
        {
            manyFields[$"field-{i}"] = "v";
        }

        var tooMany = await client.PostAsync(host.GuardUrl + "/handoff/demo", new FormUrlEncodedContent(manyFields));
        Assert.Equal(413, (int)tooMany.StatusCode);

        var longValue = await client.PostAsync(
            host.GuardUrl + "/handoff/demo",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["correlation"] = "case-limit-value",
                ["contextReference"] = new string('y', 9000)
            }));
        Assert.Equal(413, (int)longValue.StatusCode);
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
        string correlation,
        List<string>? seenUrls = null,
        Action<IPage>? onPage = null)
    {
        var page = await context.NewPageAsync();
        onPage?.Invoke(page);
        if (seenUrls is not null)
        {
            page.Request += (_, request) => seenUrls.Add(request.Url);
        }

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

    private static async Task<(string ShowPath, string Key)> ParkHandoffKeyAsync(
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
        var location = response.Headers["location"];
        Assert.DoesNotContain("k=", location, StringComparison.Ordinal);
        var setCookie = response.HeadersArray
            .First(header => string.Equals(header.Name, "set-cookie", StringComparison.OrdinalIgnoreCase)).Value;
        var pair = setCookie.Split(';', 2)[0].Split('=', 2);
        Assert.Equal(ShowModel.HandoffKeyCookieName, pair[0]);
        Assert.False(string.IsNullOrWhiteSpace(pair[1]));
        return (location, pair[1]);
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
            int handoffTtlSeconds = 60,
            int? receiveRateLimit = null,
            int? endRateLimit = null)
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
                var values = new Dictionary<string, string?>
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
                    ["ContextGuard:Profiles:demo:CorrelationFieldName"] = "correlation",
                    ["ContextGuard:Profiles:demo:MinCorrelationLength"] = "4",
                    ["ContextGuard:Profiles:demo:AllowWeakEndCorrelation"] = "true",
                    ["ContextGuard:Profiles:strict:DisplayName"] = "Strict profile",
                    ["ContextGuard:Profiles:strict:TargetUrl"] = targetUrl + "/target",
                    ["ContextGuard:Profiles:strict:Method"] = "POST",
                    ["ContextGuard:Profiles:strict:Delivery"] = "embed",
                    ["ContextGuard:Profiles:strict:AllowedFieldNames:0"] = "correlation",
                    ["ContextGuard:Profiles:strict:CorrelationFieldName"] = "correlation",
                    ["ContextGuard:Profiles:closed:DisplayName"] = "Closed profile",
                    ["ContextGuard:Profiles:closed:TargetUrl"] = targetUrl + "/target",
                    ["ContextGuard:Profiles:closed:Method"] = "POST",
                    ["ContextGuard:Profiles:closed:Delivery"] = "embed",
                    ["ContextGuard:Profiles:closed:AllowedFieldNames:0"] = "correlation",
                    ["ContextGuard:Profiles:closed:CorrelationFieldName"] = "correlation",
                    ["ContextGuard:Profiles:closed:EndEnabled"] = "false",
                    ["ContextGuard:Profiles:secret:DisplayName"] = "Secret profile",
                    ["ContextGuard:Profiles:secret:TargetUrl"] = targetUrl + "/target",
                    ["ContextGuard:Profiles:secret:Method"] = "POST",
                    ["ContextGuard:Profiles:secret:Delivery"] = "embed",
                    ["ContextGuard:Profiles:secret:AllowedFieldNames:0"] = "correlation",
                    ["ContextGuard:Profiles:secret:CorrelationFieldName"] = "correlation",
                    ["ContextGuard:Profiles:secret:EndSecret"] = "test-end-secret",
                    ["ContextGuard:Profiles:locked:DisplayName"] = "Locked profile",
                    ["ContextGuard:Profiles:locked:TargetUrl"] = targetUrl + "/target",
                    ["ContextGuard:Profiles:locked:Method"] = "POST",
                    ["ContextGuard:Profiles:locked:Delivery"] = "embed",
                    ["ContextGuard:Profiles:locked:AllowedFieldNames:0"] = "correlation",
                    ["ContextGuard:Profiles:locked:CorrelationFieldName"] = "correlation",
                    ["ContextGuard:Profiles:locked:AllowedOriginHosts:0"] = "allowed.example"
                };
                if (receiveRateLimit.HasValue)
                {
                    values["ContextGuard:ReceiveMaxRequestsPerWindow"] = receiveRateLimit.Value.ToString();
                }

                if (endRateLimit.HasValue)
                {
                    values["ContextGuard:EndMaxRequestsPerWindow"] = endRateLimit.Value.ToString();
                }

                guardBuilder.Configuration.AddInMemoryCollection(values);
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
