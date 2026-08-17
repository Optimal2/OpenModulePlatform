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

    [Fact]
    public void ToClaimsPrincipal_WithSecurityStamp_IssuesStampClaim()
    {
        var stamp = Guid.NewGuid();
        var user = new OmpAuthenticatedUser
        {
            UserId = 42,
            DisplayName = "Example User",
            Provider = "AD",
            ProviderUserKey = "sid:S-1-5-21",
            SecurityStamp = stamp
        };

        var principal = user.ToClaimsPrincipal();

        // R7-F10: the validation hook compares this claim against the account's
        // current stamp; a rotated stamp (disable, password change) must not
        // match what an old cookie carries.
        Assert.Equal(stamp.ToString(), principal.FindFirstValue(OmpAuthDefaults.SecurityStampClaimType));
    }

    [Fact]
    public void ToClaimsPrincipal_WithoutSecurityStamp_OmitsStampClaim()
    {
        var user = new OmpAuthenticatedUser
        {
            DisplayName = "External User",
            Provider = "OIDC",
            ProviderUserKey = "sub:external-1"
        };

        var principal = user.ToClaimsPrincipal();

        Assert.Null(principal.FindFirstValue(OmpAuthDefaults.SecurityStampClaimType));
    }
}
