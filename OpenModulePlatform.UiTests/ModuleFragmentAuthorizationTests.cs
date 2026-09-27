using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using OpenModulePlatform.Web.ModuleFragments;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.UiTests;

/// <summary>
/// Real Razor Pages, cookie authentication, authorization and status-page middleware
/// over loopback HTTP in Chromium. No database, external server or skip-on-failure path.
/// Only permission storage and session revocation storage are replaced with test data.
/// </summary>
[Trait("Category", "Ui")]
public sealed class ModuleFragmentAuthorizationTests
{
    [Fact]
    public async Task Attribute_alone_protects_fragment_before_rendering()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ModuleFragmentAuthorizationTests).Assembly.GetName().Name,
            EnvironmentName = "Production"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRazorPages()
            .ConfigureApplicationPartManager(parts => parts.ApplicationParts.Clear())
            .AddApplicationPart(typeof(ModuleFragmentAuthorizationTests).Assembly);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.Configure<WebAppOptions>(options => options.AllowAnonymous = false);
        builder.Services.AddAuthentication(OmpAuthDefaults.AuthenticationScheme)
            .AddCookie(OmpAuthDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = OmpAuthDefaults.CookieName;
                options.Cookie.SecurePolicy = CookieSecurePolicy.None;
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/denied";
                options.Events.OnValidatePrincipal = context =>
                {
                    if (context.Principal!.HasClaim("revoked", "true"))
                    {
                        context.RejectPrincipal();
                    }
                    return Task.CompletedTask;
                };
            });
        builder.Services.AddAuthorization(options => options.FallbackPolicy = options.DefaultPolicy);
        builder.Services.AddOmpModuleFragments();
        builder.Services.AddScoped<IOmpModuleFragmentPermissions, TestPermissions>();

        await using var app = builder.Build();
        app.UseStatusCodePages(async context =>
            await context.HttpContext.Response.WriteAsync("Status page must not render for fragments"));
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRazorPages();
        app.MapGet("/ordinary", () => "ordinary page");
        app.MapGet("/legacy", () => "unguarded fragment rendered").AllowAnonymous();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var cookieOptions = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(OmpAuthDefaults.AuthenticationScheme);

            string Cookie(string? permission = null, bool expired = false, bool revoked = false)
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "fragment-test-user") };
                if (permission is not null) claims.Add(new("permission", permission));
                if (revoked) claims.Add(new("revoked", "true"));
                var ticket = new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity(claims, OmpAuthDefaults.AuthenticationScheme)),
                    new AuthenticationProperties
                    {
                        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(expired ? -5 : 5)
                    }, OmpAuthDefaults.AuthenticationScheme);
                return cookieOptions.TicketDataFormat.Protect(ticket);
            }

            var cases = new (string? Cookie, int Status)[]
            {
                (null, 401), ("invalid-cookie", 401), (Cookie(expired: true), 401),
                (Cookie(revoked: true), 401), (Cookie(), 403),
                (Cookie("unrelated.permission"), 403),
                (Cookie("example.view"), 200), (Cookie("example.admin"), 200)
            };
            // Repeat with the Portal marker present: it is metadata, never credentials.
            foreach (var marker in new[] { false, true })
            foreach (var testCase in cases)
            {
                await using var context = await browser.NewContextAsync(new()
                {
                    ExtraHTTPHeaders = marker
                        ? new Dictionary<string, string> { ["X-OMP-Dashboard-Fragment"] = "1" }
                        : null
                });
                if (testCase.Cookie is not null)
                {
                    await context.AddCookiesAsync([new Cookie
                    {
                        Name = OmpAuthDefaults.CookieName, Value = testCase.Cookie, Url = address
                    }]);
                }
                var page = await context.NewPageAsync();
                var response = await page.GotoAsync(address + "/fragment");
                Assert.NotNull(response);
                Assert.Equal(testCase.Status, response.Status);
                Assert.Null(response.Request.RedirectedFrom);
                Assert.False(response.Headers.ContainsKey("location"));
                if (testCase.Status == 200)
                {
                    Assert.Equal(1, await page.GetByTestId("protected-fragment").CountAsync());
                }
                else
                {
                    Assert.Equal("", await response.TextAsync());
                    Assert.Equal("no-store", response.Headers["cache-control"]);
                }
            }

            await using var anonymous = await browser.NewContextAsync();
            var legacy = await anonymous.APIRequest.GetAsync(address + "/legacy");
            Assert.Equal(200, legacy.Status); // Old AllowAnonymous pattern without a guard leaks.
            var ordinary = await anonymous.APIRequest.GetAsync(address + "/ordinary", new() { MaxRedirects = 0 });
            Assert.Equal(302, ordinary.Status); // Ordinary pages keep their login redirect.
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Fragment_combined_with_AllowAnonymous_fails_host_startup()
    {
        var builder = CreateFragmentAppBuilder(
            "Production",
            configureRazorPages: options => options.RootDirectory = "/ConflictPages");
        builder.Services.Configure<WebAppOptions>(options => options.AllowAnonymous = false);

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRazorPages();
        // Same mistake through endpoint conventions instead of an attribute.
        app.MapGet("/fragment-conflict", () => "conflicting fragment")
            .WithMetadata(new OmpModuleFragmentAttribute("example.view"))
            .AllowAnonymous();
        // Anonymous without the fragment attribute stays legal.
        app.MapGet("/legacy", () => "unguarded").AllowAnonymous();

        try
        {
            var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => app.StartAsync());
            // The Razor page and the minimal API endpoint must both be named in the failure.
            Assert.Contains("FragmentAnonymous", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/fragment-conflict", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Anonymous_bypass_is_refused_outside_Development_without_explicit_opt_in()
    {
        var logs = new CapturedLogs();
        var appHost = await StartFragmentAppAsync(
            "Production",
            configureWebApp: options => options.AllowAnonymous = true,
            logs);
        try
        {
            Assert.Equal(401, await GetStatusAsync(appHost.Address, "/fragment"));
            Assert.Equal(403, await GetStatusAsync(appHost.Address, "/fragment",
                ProtectFragmentTicket(appHost.App, "unrelated.permission")));
            Assert.Equal(200, await GetStatusAsync(appHost.Address, "/fragment",
                ProtectFragmentTicket(appHost.App, "example.view")));
            Assert.Contains(logs.Warnings, message => message.Contains("AllowAnonymous"));
        }
        finally
        {
            await appHost.App.StopAsync();
            await appHost.App.DisposeAsync();
        }
    }

    [Fact]
    public async Task Anonymous_bypass_is_honored_in_Development_with_startup_warning()
    {
        var logs = new CapturedLogs();
        var appHost = await StartFragmentAppAsync(
            "Development",
            configureWebApp: options => options.AllowAnonymous = true,
            logs);
        try
        {
            // The development bypass renders the fragment without any session.
            Assert.Equal(200, await GetStatusAsync(appHost.Address, "/fragment"));
            Assert.Contains(logs.Warnings, message => message.Contains("AllowAnonymous"));
        }
        finally
        {
            await appHost.App.StopAsync();
            await appHost.App.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("WebApp", "WebApp", 200)]
    [InlineData("Portal", "Portal", 200)]
    [InlineData("Portal", "WebApp", 401)]
    [InlineData("WebApp", "Portal", 401)]
    public async Task Anonymous_bypass_outside_Development_requires_explicit_opt_in(
        string optionsSectionName, string overrideSectionName, int expectedStatus)
    {
        // Set through configuration so the flag travels the same binding path a
        // deployed app uses, including apps that choose a custom options section.
        var builder = CreateFragmentAppBuilder(
            "Production",
            configureWebApp: options => options.AllowAnonymous = true,
            optionsSectionName: optionsSectionName);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{overrideSectionName}:AllowAnonymousOutsideDevelopment"] = "true"
        });
        var logs = new CapturedLogs();
        builder.Services.AddSingleton<ILoggerProvider>(logs);

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRazorPages();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        try
        {
            Assert.Equal(expectedStatus, await GetStatusAsync(address, "/fragment"));
            Assert.Contains(logs.Warnings, message => message.Contains(expectedStatus == 200
                ? "bypassed outside" : "refused and permissions are enforced"));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private readonly record struct FragmentAppHost(WebApplication App, string Address);

    private static async Task<FragmentAppHost> StartFragmentAppAsync(
        string environmentName,
        Action<WebAppOptions>? configureWebApp,
        CapturedLogs logs)
    {
        var builder = CreateFragmentAppBuilder(environmentName, configureWebApp: configureWebApp);
        builder.Services.AddSingleton<ILoggerProvider>(logs);

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRazorPages();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        return new FragmentAppHost(app, address);
    }

    private static async Task<int> GetStatusAsync(string address, string path, string? cookie = null)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, address + path);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{OmpAuthDefaults.CookieName}={cookie}");
        }
        using var response = await client.SendAsync(request);
        return (int)response.StatusCode;
    }

    private static string ProtectFragmentTicket(WebApplication app, string? permission = null)
    {
        var cookieOptions = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(OmpAuthDefaults.AuthenticationScheme);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "fragment-test-user") };
        if (permission is not null)
        {
            claims.Add(new("permission", permission));
        }
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, OmpAuthDefaults.AuthenticationScheme)),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) },
            OmpAuthDefaults.AuthenticationScheme);
        return cookieOptions.TicketDataFormat.Protect(ticket);
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturedLogger(this);

        public void Dispose() { }

        private sealed class CapturedLogger(CapturedLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                {
                    owner.Warnings.Add(formatter(state, exception));
                }
            }
        }
    }

    private static WebApplicationBuilder CreateFragmentAppBuilder(
        string environmentName,
        Action<RazorPagesOptions>? configureRazorPages = null,
        Action<WebAppOptions>? configureWebApp = null,
        string optionsSectionName = WebAppOptions.DefaultSectionName)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ModuleFragmentAuthorizationTests).Assembly.GetName().Name,
            EnvironmentName = environmentName
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRazorPages(configureRazorPages ?? (_ => { }))
            .ConfigureApplicationPartManager(parts => parts.ApplicationParts.Clear())
            .AddApplicationPart(typeof(ModuleFragmentAuthorizationTests).Assembly);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddOptions<WebAppOptions>().Bind(builder.Configuration.GetSection(optionsSectionName));
        builder.Services.Configure<WebAppOptions>(configureWebApp ?? (_ => { }));
        builder.Services.AddAuthentication(OmpAuthDefaults.AuthenticationScheme)
            .AddCookie(OmpAuthDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = OmpAuthDefaults.CookieName;
                options.Cookie.SecurePolicy = CookieSecurePolicy.None;
            });
        builder.Services.AddAuthorization(options => options.FallbackPolicy = options.DefaultPolicy);
        builder.Services.AddOmpModuleFragments();
        // Replace the default RBAC permission source so the fixture needs no database.
        // RemoveAll rather than a second AddScoped: the Development environment
        // validates every service descriptor at build time and would try to
        // construct the default (database-backed) implementation.
        builder.Services.RemoveAll<IOmpModuleFragmentPermissions>();
        builder.Services.AddScoped<IOmpModuleFragmentPermissions, TestPermissions>();
        return builder;
    }

    private sealed class TestPermissions : IOmpModuleFragmentPermissions
    {
        public Task<HashSet<string>> GetAsync(HttpContext context)
            => Task.FromResult(context.User.FindAll("permission").Select(claim => claim.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}
