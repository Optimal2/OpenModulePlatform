using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class OmpOidcClaimResolverTests
{
    [Fact]
    public void Resolve_UsesConfiguredClaims()
    {
        var principal = CreatePrincipal(
            new Claim("iss", "https://issuer.example"),
            new Claim("oid", "user-123"),
            new Claim("upn", "user@example.test"),
            new Claim("display_name", "Example User"),
            new Claim("roles", "Group A"),
            new Claim("roles", "Group A"),
            new Claim("roles", "Group B"));
        var options = new OmpOidcOptions
        {
            ClaimTypes = new OmpOidcClaimTypeOptions
            {
                UserIdClaimType = "oid",
                NameClaimType = "upn",
                DisplayNameClaimType = "display_name",
                GroupsClaimType = "roles"
            }
        };

        var resolved = OmpOidcClaimResolver.Resolve(principal, options);

        Assert.NotNull(resolved);
        Assert.Equal("user-123", resolved.Subject);
        Assert.Equal("https://issuer.example|user-123", resolved.ProviderUserKey);
        Assert.Equal("user@example.test", resolved.UserName);
        Assert.Equal("Example User", resolved.DisplayName);
        Assert.Equal(2, resolved.Groups.Count);
        Assert.Contains("Group A", resolved.Groups);
        Assert.Contains("Group B", resolved.Groups);
    }

    [Fact]
    public void Resolve_ReturnsNullWhenUserIdClaimIsMissing()
    {
        var principal = CreatePrincipal(new Claim("name", "Example User"));

        var resolved = OmpOidcClaimResolver.Resolve(principal, new OmpOidcOptions());

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_FallsBackToSubjectForNameAndDisplayName()
    {
        var principal = CreatePrincipal(new Claim("sub", "subject-1"));

        var resolved = OmpOidcClaimResolver.Resolve(principal, new OmpOidcOptions());

        Assert.NotNull(resolved);
        Assert.Equal("subject-1", resolved.UserName);
        Assert.Equal("subject-1", resolved.DisplayName);
        Assert.Equal("subject-1", resolved.ProviderUserKey);
    }

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "oidc"));
}
