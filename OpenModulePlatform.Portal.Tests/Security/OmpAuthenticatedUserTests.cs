using OpenModulePlatform.Auth.Models;
using OpenModulePlatform.Web.Shared.Security;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class OmpAuthenticatedUserTests
{
    [Fact]
    public void ToClaimsPrincipal_IssuesOmpCookiePrincipalWithoutExternalTokens()
    {
        var user = new OmpAuthenticatedUser
        {
            UserId = 42,
            DisplayName = "Example User",
            Provider = "ADFS",
            ProviderUserKey = "https://idp.local.test/adfs|user-123",
            RolePrincipals =
            [
                ("ADUser", "example.user@example.test"),
                ("ADGroup", "S-1-5-32-544"),
                ("", "ignored"),
                ("ADGroup", "")
            ]
        };

        var principal = user.ToClaimsPrincipal();

        Assert.Equal(OmpAuthDefaults.AuthenticationScheme, principal.Identity?.AuthenticationType);
        Assert.Equal("Example User", principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal("42", principal.FindFirstValue(OmpAuthDefaults.UserIdClaimType));
        Assert.Equal("ADFS", principal.FindFirstValue(OmpAuthDefaults.ProviderClaimType));
        Assert.Equal(
            "https://idp.local.test/adfs|user-123",
            principal.FindFirstValue(OmpAuthDefaults.ProviderUserKeyClaimType));
        Assert.Contains(
            principal.Claims,
            claim => claim.Type == OmpAuthDefaults.PrincipalClaimType &&
                     claim.Value == "ADUser|example.user@example.test");
        Assert.Contains(
            principal.Claims,
            claim => claim.Type == OmpAuthDefaults.PrincipalClaimType &&
                     claim.Value == "ADGroup|S-1-5-32-544");
        Assert.DoesNotContain(
            principal.Claims,
            claim => claim.Type == OmpAuthDefaults.PrincipalClaimType &&
                     claim.Value.Contains("ignored", StringComparison.Ordinal));
    }
}
