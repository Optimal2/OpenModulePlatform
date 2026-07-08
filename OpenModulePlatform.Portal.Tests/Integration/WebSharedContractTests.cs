using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Notifications;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using System.Net;

namespace OpenModulePlatform.Portal.Tests.Integration;

/// <summary>
/// Runtime contract tests that verify Portal wires up the shared
/// OpenModulePlatform.Web.Shared hosting defaults correctly.
/// </summary>
public sealed class WebSharedContractTests : IClassFixture<PushEventPipelineTestFixture>
{
    private readonly PushEventPipelineTestFixture _fixture;

    public WebSharedContractTests(PushEventPipelineTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OmpWebDefaults_RegistersCookieAuthentication()
    {
        var services = _fixture.Factory.Services;

        var authOptions = services.GetRequiredService<IOptions<OmpAuthOptions>>();
        Assert.NotNull(authOptions.Value);

        var schemeProvider = services.GetRequiredService<IAuthenticationSchemeProvider>();
        var cookieScheme = await schemeProvider.GetSchemeAsync(OmpAuthDefaults.AuthenticationScheme);
        Assert.NotNull(cookieScheme);
    }

    [Fact]
    public void OmpWebDefaults_RegistersRequestLocalization()
    {
        var services = _fixture.Factory.Services;

        var localizationOptions = services.GetRequiredService<IOptions<RequestLocalizationOptions>>();
        Assert.NotNull(localizationOptions.Value);
        Assert.NotNull(localizationOptions.Value.SupportedCultures);
        Assert.NotEmpty(localizationOptions.Value.SupportedCultures);
    }

    [Fact]
    public void OmpWebDefaults_RegistersTopBarServices()
    {
        var services = _fixture.Factory.Services;

        var publisher = services.GetRequiredService<ITopBarNotificationStatePublisher>();
        Assert.NotNull(publisher);

        using var scope = services.CreateScope();
        var topBarService = scope.ServiceProvider.GetRequiredService<PortalTopBarService>();
        Assert.NotNull(topBarService);
    }

    [Fact]
    public async Task StatusPage_RendersWithoutAuthentication()
    {
        using var client = _fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // The shared status page is [AllowAnonymous] and uses the layout pipeline
        // (including Web.Shared topbar components). The page echoes the requested
        // status code, so 404 is a valid lightweight render path.
        using var response = await client.GetAsync("/status/404");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(content));
    }
}
