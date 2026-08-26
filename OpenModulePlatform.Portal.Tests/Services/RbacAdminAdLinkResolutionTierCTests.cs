// File: OpenModulePlatform.Portal.Tests/Services/RbacAdminAdLinkResolutionTierCTests.cs
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Tier C tests (real SQL Server) for the AD-user-principal resolution used by
/// the RBAC role admin page (campaign ad-principalformen-hela-vagen-adfs-till-rbac,
/// follow-up phase 2, finding 1). Production uniqueness on omp.user_auth is
/// (provider_id, provider_user_hash) — a SHA-256 over the raw key, case-sensitive —
/// while equality lookups are collation-based and case-insensitive, so two active
/// AD links differing only in letter case resolve to two different OMP users.
/// The resolution must report that ambiguity so the caller can abstain instead
/// of silently picking one user. All principals and domains are invented test
/// data, never real customer data.
/// </summary>
[Collection(AdPrincipalMigrationCollection.CollectionName)]
public sealed class RbacAdminAdLinkResolutionTierCTests(AdPrincipalMigrationTestFixture fixture)
{
    [Fact]
    public async Task Resolve_TwoActiveLinksDifferingOnlyInCase_ReportsAmbiguity()
    {
        var userA = await fixture.InsertUserAsync("TierC Case Anna", active: true);
        var userB = await fixture.InsertUserAsync("TierC Case ANNA", active: true);
        await fixture.InsertAuthLinkAsync(userA, "AD", @"CONTOSO\rc-anna");
        await fixture.InsertAuthLinkAsync(userB, "AD", @"CONTOSO\RC-ANNA");

        var resolution = await fixture.CreateRbacAdminRepository()
            .GetLinkedActiveOmpUsersForAdUserPrincipalAsync(@"CONTOSO\rc-anna", CancellationToken.None);

        Assert.Equal(2, resolution.ActiveUserCount);
        Assert.Null(resolution.UniqueUserId);
    }

    [Fact]
    public async Task Resolve_SingleActiveLink_ReturnsThatUser()
    {
        var userId = await fixture.InsertUserAsync("TierC Single User", active: true);
        await fixture.InsertAuthLinkAsync(userId, "AD", @"CONTOSO\rc-single");

        // An inactive user and a disabled link must not create an ambiguity,
        // mirroring the sign-in path where only active users count.
        var inactiveUser = await fixture.InsertUserAsync("TierC Single Inactive", active: false);
        await fixture.InsertAuthLinkAsync(inactiveUser, "AD", @"CONTOSO\RC-SINGLE");
        var disabledLinkUser = await fixture.InsertUserAsync("TierC Single DisabledLink", active: true);
        await fixture.InsertAuthLinkAsync(disabledLinkUser, "AD", @"CONTOSO\rc-SINGLE", authStatus: "disabled");

        var resolution = await fixture.CreateRbacAdminRepository()
            .GetLinkedActiveOmpUsersForAdUserPrincipalAsync(@"CONTOSO\rc-single", CancellationToken.None);

        Assert.Equal(1, resolution.ActiveUserCount);
        Assert.Equal(userId, resolution.UniqueUserId);
    }

    [Fact]
    public async Task Resolve_NoLink_ReturnsNone()
    {
        var resolution = await fixture.CreateRbacAdminRepository()
            .GetLinkedActiveOmpUsersForAdUserPrincipalAsync(@"CONTOSO\rc-missing", CancellationToken.None);

        Assert.Equal(0, resolution.ActiveUserCount);
        Assert.Null(resolution.UniqueUserId);
    }
}
