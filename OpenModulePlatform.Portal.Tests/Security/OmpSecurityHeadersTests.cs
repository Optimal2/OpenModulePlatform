// File: OpenModulePlatform.Portal.Tests/Security/OmpSecurityHeadersTests.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Security;
using System.Net;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// Contract tests for the Content-Security-Policy support in
/// UseOmpSecurityHeaders. They host a minimal in-memory pipeline (no
/// database) and assert the header emission rules: report-only by default,
/// enforcement via configuration, per-app policy override, and the
/// set-earlier-wins behavior shared with the other security headers.
/// </summary>
public sealed class OmpSecurityHeadersTests
{
    private static async Task<(WebApplication app, HttpClient client)> StartAppAsync(
        Dictionary<string, string?>? config = null,
        string optionsSectionName = "WebApp",
        Action<WebApplication>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (config is not null)
        {
            builder.Configuration.AddInMemoryCollection(config);
        }

        var app = builder.Build();
        app.UseOmpSecurityHeaders(optionsSectionName);
        configure?.Invoke(app);
        app.MapGet("/", () => "ok");
        app.MapOmpCspReportEndpoint();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task Default_EmitsBaselineAsReportOnly()
    {
        var (app, client) = await StartAppAsync();
        await using var _ = app;

        var response = await client.GetAsync("/");

        Assert.False(response.Headers.Contains("Content-Security-Policy"));
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy-Report-Only"));
        Assert.Contains("default-src 'self'", policy);
        Assert.Contains("object-src 'none'", policy);
        Assert.Contains("frame-ancestors 'self'", policy);
        Assert.Contains($"report-uri {OmpContentSecurityPolicy.ReportPath}", policy);
        // The other security headers still come along unchanged.
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task ReportOnlyFalse_EmitsEnforcingHeader()
    {
        var (app, client) = await StartAppAsync(new Dictionary<string, string?>
        {
            ["WebApp:SecurityHeaders:ContentSecurityPolicy:ReportOnly"] = "false"
        });
        await using var _ = app;

        var response = await client.GetAsync("/");

        Assert.False(response.Headers.Contains("Content-Security-Policy-Report-Only"));
        Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
    }

    [Fact]
    public async Task ConfiguredPolicy_OverridesBaseline()
    {
        const string strict = "default-src 'none'; script-src 'self'; frame-ancestors 'none'";
        var (app, client) = await StartAppAsync(new Dictionary<string, string?>
        {
            ["WebApp:SecurityHeaders:ContentSecurityPolicy:Policy"] = strict
        });
        await using var _ = app;

        var response = await client.GetAsync("/");

        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy-Report-Only"));
        Assert.StartsWith(strict, policy);
        Assert.Contains("report-uri", policy);
    }

    [Fact]
    public async Task CustomOptionsSection_IsHonored()
    {
        // Portal and the modules bind their web options from the "Portal"
        // section; the middleware must read the CSP config from the same
        // section the app passes to UseOmpWebDefaults.
        var (app, client) = await StartAppAsync(
            new Dictionary<string, string?>
            {
                ["Portal:SecurityHeaders:ContentSecurityPolicy:Policy"] = "default-src 'none'"
            },
            optionsSectionName: "Portal");
        await using var _ = app;

        var response = await client.GetAsync("/");

        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy-Report-Only"));
        Assert.StartsWith("default-src 'none'", policy);
    }

    [Fact]
    public async Task HeaderSetEarlierByApp_Wins()
    {
        var (app, client) = await StartAppAsync(configure: app =>
            app.Use((context, next) =>
            {
                context.Response.Headers["Content-Security-Policy-Report-Only"] = "default-src 'none'";
                return next();
            }));
        await using var _ = app;

        var response = await client.GetAsync("/");

        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy-Report-Only"));
        Assert.Equal("default-src 'none'", policy);
    }

    [Fact]
    public async Task Disabled_EmitsNoCspHeader()
    {
        var (app, client) = await StartAppAsync(new Dictionary<string, string?>
        {
            ["WebApp:SecurityHeaders:ContentSecurityPolicy:Enabled"] = "false"
        });
        await using var _ = app;

        var response = await client.GetAsync("/");

        Assert.False(response.Headers.Contains("Content-Security-Policy"));
        Assert.False(response.Headers.Contains("Content-Security-Policy-Report-Only"));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task ReportEndpoint_AcceptsBrowserReportPayload()
    {
        var (app, client) = await StartAppAsync();
        await using var _ = app;

        var report = new StringContent(
            """{"csp-report":{"document-uri":"http://localhost/","violated-directive":"script-src","blocked-uri":"inline"}}""",
            System.Text.Encoding.UTF8,
            "application/csp-report");

        var response = await client.PostAsync(OmpContentSecurityPolicy.ReportPath, report);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ReportEndpoint_ToleratesEmptyBody()
    {
        var (app, client) = await StartAppAsync();
        await using var _ = app;

        var response = await client.PostAsync(
            OmpContentSecurityPolicy.ReportPath,
            new StringContent("", System.Text.Encoding.UTF8, "application/csp-report"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
