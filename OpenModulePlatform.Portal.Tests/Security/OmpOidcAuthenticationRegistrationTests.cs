using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class OmpOidcAuthenticationRegistrationTests
{
    [Fact]
    public async Task AddOmpOidcAuthentication_WhenDisabled_DoesNotRegisterOidcScheme()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OmpAuth:Oidc:Enabled"] = "false"
        });
        var services = CreateBaseServices(configuration);

        var status = services.AddOmpOidcAuthentication(configuration);

        using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.False(status.IsEnabled);
        Assert.Equal("OpenID Connect", status.DisplayName);
        Assert.False(provider.GetRequiredService<OmpOidcProviderStatus>().IsEnabled);
        Assert.NotNull(await schemeProvider.GetSchemeAsync(OmpAuthDefaults.AuthenticationScheme));
        Assert.Null(await schemeProvider.GetSchemeAsync(OmpAuthDefaults.OidcAuthenticationScheme));
    }

    [Fact]
    public void AddOmpOidcAuthentication_WhenRequiredSettingsAreMissing_FailsFast()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OmpAuth:Oidc:Enabled"] = "true",
            ["OmpAuth:Oidc:DisplayName"] = "Local AD FS",
            ["OmpAuth:Oidc:ClientId"] = "omp-local-auth"
        });
        var services = CreateBaseServices(configuration);

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddOmpOidcAuthentication(configuration));

        Assert.Contains("OmpAuth:Oidc requires Authority or MetadataAddress.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddOmpOidcAuthentication_WithLocalSimulatedConfig_RegistersSafeOidcOptions()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OmpAuth:Oidc:Enabled"] = "true",
            ["OmpAuth:Oidc:DisplayName"] = "Local simulated AD FS",
            ["OmpAuth:Oidc:ProviderName"] = "ADFS",
            ["OmpAuth:Oidc:Authority"] = "https://idp.local.test/adfs",
            ["OmpAuth:Oidc:ClientId"] = "omp-local-auth",
            ["OmpAuth:Oidc:ClientSecret"] = "local-test-client-secret-placeholder",
            ["OmpAuth:Oidc:CallbackPath"] = "/signin-oidc",
            ["OmpAuth:Oidc:ResponseType"] = "code",
            ["OmpAuth:Oidc:Scopes:0"] = "openid",
            ["OmpAuth:Oidc:Scopes:1"] = "profile",
            ["OmpAuth:Oidc:Scopes:2"] = "allatclaims",
            ["OmpAuth:Oidc:ClaimTypes:NameClaimType"] = "upn",
            ["OmpAuth:Oidc:ClaimTypes:GroupsClaimType"] = "roles"
        });
        var services = CreateBaseServices(configuration);

        var status = services.AddOmpOidcAuthentication(configuration);

        using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var options = provider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OmpAuthDefaults.OidcAuthenticationScheme);

        Assert.True(status.IsEnabled);
        Assert.Equal("Local simulated AD FS", status.DisplayName);
        Assert.NotNull(await schemeProvider.GetSchemeAsync(OmpAuthDefaults.OidcAuthenticationScheme));
        Assert.Equal(OmpAuthDefaults.AuthenticationScheme, options.SignInScheme);
        Assert.Equal("https://idp.local.test/adfs", options.Authority);
        Assert.Equal("omp-local-auth", options.ClientId);
        Assert.Equal("/signin-oidc", options.CallbackPath.Value);
        Assert.Equal(OpenIdConnectResponseType.Code, options.ResponseType);
        Assert.True(options.UsePkce);
        Assert.False(options.SaveTokens);
        Assert.False(options.GetClaimsFromUserInfoEndpoint);
        Assert.False(options.MapInboundClaims);
        Assert.Contains("openid", options.Scope);
        Assert.Contains("profile", options.Scope);
        Assert.Contains("allatclaims", options.Scope);
        Assert.Equal("upn", options.TokenValidationParameters.NameClaimType);
        Assert.Equal("roles", options.TokenValidationParameters.RoleClaimType);
    }

    private static IServiceCollection CreateBaseServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOmpCookieAuthentication(configuration);
        return services;
    }

    private static IConfiguration CreateConfiguration(IReadOnlyDictionary<string, string?> values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
