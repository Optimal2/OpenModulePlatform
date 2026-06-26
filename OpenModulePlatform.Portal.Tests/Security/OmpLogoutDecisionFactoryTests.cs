using System.Security.Claims;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class OmpLogoutDecisionFactoryTests
{
    [Fact]
    public void Create_ForConfiguredOidcProvider_InvokesRemoteSignOut()
    {
        var decision = OmpLogoutDecisionFactory.Create(
            CreateUser("ADFS"),
            new OmpAuthOptions
            {
                LoginPath = "/auth/login",
                Oidc = new OmpOidcOptions
                {
                    ProviderName = "ADFS"
                }
            },
            new OmpOidcProviderStatus
            {
                IsEnabled = true,
                DisplayName = "AD FS"
            });

        Assert.True(decision.SignOutOidc);
        Assert.Equal("/auth/login?logout=oidc", decision.RedirectUri);
        Assert.Equal("ADFS", decision.Provider);
    }

    [Fact]
    public void Create_ForWindowsProvider_KeepsLogoutLocal()
    {
        var decision = OmpLogoutDecisionFactory.Create(
            CreateUser(OmpAuthDefaults.AdProviderDisplayName),
            new OmpAuthOptions
            {
                LoginPath = "/auth/login",
                Oidc = new OmpOidcOptions
                {
                    ProviderName = "ADFS"
                }
            },
            new OmpOidcProviderStatus
            {
                IsEnabled = true,
                DisplayName = "AD FS"
            });

        Assert.False(decision.SignOutOidc);
        Assert.Equal("/auth/login?logout=windows", decision.RedirectUri);
    }

    [Fact]
    public void Create_WhenOidcSchemeIsDisabled_KeepsExternalProviderLogoutLocal()
    {
        var decision = OmpLogoutDecisionFactory.Create(
            CreateUser("ADFS"),
            new OmpAuthOptions
            {
                LoginPath = "/auth/login",
                Oidc = new OmpOidcOptions
                {
                    ProviderName = "ADFS"
                }
            },
            new OmpOidcProviderStatus
            {
                IsEnabled = false,
                DisplayName = "AD FS"
            });

        Assert.False(decision.SignOutOidc);
        Assert.Equal("/auth/login?logout=local", decision.RedirectUri);
    }

    [Theory]
    [InlineData("https://example.test/login")]
    [InlineData("//example.test/login")]
    [InlineData("/auth\\login")]
    [InlineData("/auth/login?returnUrl=/")]
    public void Create_WithUnsafeConfiguredLoginPath_FallsBackToDefault(string loginPath)
    {
        var decision = OmpLogoutDecisionFactory.Create(
            CreateUser(LocalPasswordIdentity.ProviderDisplayName),
            new OmpAuthOptions
            {
                LoginPath = loginPath
            },
            new OmpOidcProviderStatus());

        Assert.Equal("/auth/login?logout=local", decision.RedirectUri);
    }

    private static ClaimsPrincipal CreateUser(string provider)
        => new(new ClaimsIdentity(
            [new Claim(OmpAuthDefaults.ProviderClaimType, provider)],
            OmpAuthDefaults.AuthenticationScheme));
}
