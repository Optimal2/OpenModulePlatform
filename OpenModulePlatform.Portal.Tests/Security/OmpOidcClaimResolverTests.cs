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
                ProviderUserKeyClaimType = "oid",
                UserIdClaimType = "oid",
                NameClaimType = "upn",
                DisplayNameClaimType = "display_name",
                GroupsClaimType = "roles"
            }
        };

        var resolved = OmpOidcClaimResolver.Resolve(principal, options);

        Assert.NotNull(resolved);
        Assert.Equal("OIDC", resolved.ProviderName);
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

    [Fact]
    public void Resolve_UsesConfiguredProviderNameAndProviderUserKeyClaim()
    {
        var principal = CreatePrincipal(
            new Claim("iss", "https://issuer.example"),
            new Claim("sub", "subject-1"),
            new Claim("objectsid", "S-1-5-21-1000"));
        var options = new OmpOidcOptions
        {
            ProviderName = "ADFS",
            ClaimTypes = new OmpOidcClaimTypeOptions
            {
                ProviderUserKeyClaimType = "objectsid",
                UserIdClaimType = "sub"
            }
        };

        var resolved = OmpOidcClaimResolver.Resolve(principal, options);

        Assert.NotNull(resolved);
        Assert.Equal("ADFS", resolved.ProviderName);
        Assert.Equal("subject-1", resolved.Subject);
        Assert.Equal("https://issuer.example|S-1-5-21-1000", resolved.ProviderUserKey);
        Assert.Contains("sid:S-1-5-21-1000", resolved.ProviderUserKeyCandidates);
    }

    [Fact]
    public void Resolve_BuildsAdStyleUserPrincipalCandidates()
    {
        var principal = CreatePrincipal(
            new Claim("sub", "subject-1"),
            new Claim("upn", "user@example.test"),
            new Claim("sam", "jdoe"),
            new Claim("netbios", "EXAMPLE"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1000"));
        var options = new OmpOidcOptions
        {
            ClaimTypes = new OmpOidcClaimTypeOptions
            {
                SamAccountNameClaimType = "sam",
                DomainClaimType = "netbios"
            }
        };

        var resolved = OmpOidcClaimResolver.Resolve(principal, options);

        Assert.NotNull(resolved);
        Assert.Contains("S-1-5-21-1000", resolved.UserPrincipalCandidates);
        Assert.Contains("user@example.test", resolved.UserPrincipalCandidates);
        Assert.Contains("jdoe", resolved.UserPrincipalCandidates);
        Assert.Contains("EXAMPLE\\jdoe", resolved.UserPrincipalCandidates);
        Assert.Contains("upn:user@example.test", resolved.ProviderUserKeyCandidates);
        Assert.Contains("name:EXAMPLE\\jdoe", resolved.ProviderUserKeyCandidates);
    }

    [Fact]
    public void Resolve_ReadsGroupsFromConfiguredMultipleClaimTypes()
    {
        var principal = CreatePrincipal(
            new Claim("sub", "subject-1"),
            new Claim("groups", "Group A"),
            new Claim("group_sid", "S-1-5-32-544"),
            new Claim("group_name", "EXAMPLE\\Admins"));
        var options = new OmpOidcOptions
        {
            ClaimTypes = new OmpOidcClaimTypeOptions
            {
                GroupSidClaimTypes = ["group_sid"],
                GroupNameClaimTypes = ["group_name"]
            }
        };

        var resolved = OmpOidcClaimResolver.Resolve(principal, options);

        Assert.NotNull(resolved);
        Assert.Contains("Group A", resolved.Groups);
        Assert.Contains("S-1-5-32-544", resolved.Groups);
        Assert.Contains("EXAMPLE\\Admins", resolved.Groups);
    }

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "oidc"));
}
