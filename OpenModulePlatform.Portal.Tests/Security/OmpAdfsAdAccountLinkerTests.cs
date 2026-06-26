using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class OmpAdfsAdAccountLinkerTests
{
    [Fact]
    public void BuildAdProviderLookupKeys_IncludesDomainSamAliases()
    {
        var claims = CreateClaims(
            providerUserKeyCandidates:
            [
                "https://issuer.example|subject-1",
                "sub:subject-1",
                "name:Example User",
                "name:EXAMPLE\\jdoe",
                "EXAMPLE\\jdoe"
            ],
            userPrincipalCandidates: ["jdoe", "EXAMPLE\\jdoe"]);

        var keys = OmpAdfsAdAccountLinker.BuildAdProviderLookupKeys(claims);

        Assert.Contains("name:EXAMPLE\\jdoe", keys);
        Assert.Contains("EXAMPLE\\jdoe", keys);
        Assert.DoesNotContain("name:Example User", keys);
        Assert.DoesNotContain("sub:subject-1", keys);
        Assert.DoesNotContain("https://issuer.example|subject-1", keys);
    }

    [Fact]
    public void BuildAdProviderLookupKeys_IncludesSidAlias()
    {
        var claims = CreateClaims(
            providerUserKeyCandidates: ["sid:S-1-5-21-1000"],
            userPrincipalCandidates: ["S-1-5-21-1000"]);

        var keys = OmpAdfsAdAccountLinker.BuildAdProviderLookupKeys(claims);

        Assert.Contains("sid:S-1-5-21-1000", keys);
        Assert.Single(keys);
    }

    [Fact]
    public void BuildAdProviderLookupKeys_IncludesUpnAliasButExcludesRawEmail()
    {
        var claims = CreateClaims(
            providerUserKeyCandidates: ["upn:jane@example.test", "jane@example.test"],
            userPrincipalCandidates: ["jane@example.test"]);

        var keys = OmpAdfsAdAccountLinker.BuildAdProviderLookupKeys(claims);

        Assert.Contains("upn:jane@example.test", keys);
        Assert.DoesNotContain("jane@example.test", keys);
        Assert.Single(keys);
    }

    [Fact]
    public void Resolve_WhenNoAdMatch_PreservesNoMatchBehavior()
    {
        var resolution = OmpAdfsAdAccountLinker.Resolve([]);

        Assert.Equal(OmpAdLinkedUserResolutionStatus.NoMatch, resolution.Status);
        Assert.Null(resolution.User);
    }

    [Fact]
    public void Resolve_WhenOneActiveUserMatches_ReturnsUniqueActiveUser()
    {
        var resolution = OmpAdfsAdAccountLinker.Resolve(
        [
            new OmpAdLinkedUserCandidate(10, 42, "Jane Doe", 1),
            new OmpAdLinkedUserCandidate(11, 42, "Jane Doe", 1)
        ]);

        Assert.Equal(OmpAdLinkedUserResolutionStatus.UniqueActive, resolution.Status);
        Assert.Equal(42, resolution.User?.UserId);
        Assert.Equal(1, resolution.ActiveUserCount);
    }

    [Fact]
    public void Resolve_WhenMultipleActiveUsersMatch_IsAmbiguous()
    {
        var resolution = OmpAdfsAdAccountLinker.Resolve(
        [
            new OmpAdLinkedUserCandidate(10, 42, "Jane Doe", 1),
            new OmpAdLinkedUserCandidate(11, 43, "Jane Duplicate", 1)
        ]);

        Assert.Equal(OmpAdLinkedUserResolutionStatus.AmbiguousActive, resolution.Status);
        Assert.Null(resolution.User);
        Assert.Equal(2, resolution.ActiveUserCount);
    }

    [Fact]
    public void Resolve_WhenOnlyDisabledUsersMatch_BlocksFallback()
    {
        var resolution = OmpAdfsAdAccountLinker.Resolve(
        [
            new OmpAdLinkedUserCandidate(10, 42, "Jane Doe", 0)
        ]);

        Assert.Equal(OmpAdLinkedUserResolutionStatus.Disabled, resolution.Status);
        Assert.Equal(42, resolution.User?.UserId);
        Assert.Equal(0, resolution.ActiveUserCount);
    }

    private static OmpOidcResolvedClaims CreateClaims(
        IReadOnlyList<string> providerUserKeyCandidates,
        IReadOnlyList<string> userPrincipalCandidates)
        => new()
        {
            ProviderName = OmpAuthDefaults.AdfsProviderDisplayName,
            ProviderUserKey = "https://issuer.example|subject-1",
            ProviderUserKeyCandidates = providerUserKeyCandidates,
            Subject = "subject-1",
            UserName = "Example User",
            DisplayName = "Example User",
            UserPrincipalCandidates = userPrincipalCandidates,
            Groups = []
        };
}
