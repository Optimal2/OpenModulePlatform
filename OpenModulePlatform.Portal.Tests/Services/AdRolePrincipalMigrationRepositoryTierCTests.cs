// File: OpenModulePlatform.Portal.Tests/Services/AdRolePrincipalMigrationRepositoryTierCTests.cs
using System.Globalization;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Tier C tests (real SQL Server) for the bulk AD role-principal migration
/// repository (campaign ad-principalformen-hela-vagen-adfs-till-rbac, DEL 3).
/// All principals, domains and SIDs below are invented test data, never real
/// customer data.
/// </summary>
[Collection(AdPrincipalMigrationCollection.CollectionName)]
public sealed class AdRolePrincipalMigrationRepositoryTierCTests(AdPrincipalMigrationTestFixture fixture)
{
    [Fact]
    public async Task Preview_MatchesExecuteOutcome()
    {
        var userId = await fixture.InsertUserAsync("TierC PreviewMatch User", active: true);
        await fixture.InsertAuthLinkAsync(userId, "AD", @"CONTOSO\tc-preview");
        await fixture.InsertAuthLinkAsync(userId, "LocalPassword", "tc-preview@example.invalid");
        var roleId = await fixture.InsertRoleAsync("TierC PreviewMatch Role");
        await fixture.InsertRolePrincipalAsync(roleId, "ADUser", @"CONTOSO\tc-preview");
        await fixture.InsertRolePrincipalAsync(roleId, "ADUser", @"CONTOSO\tc-no-link");

        var preview = await fixture.CreateRepository().PreviewAsync(CancellationToken.None);

        var previewMove = Assert.Single(preview, row =>
            row.RoleId == roleId && row.Principal == @"CONTOSO\tc-preview");
        Assert.Equal(AdRolePrincipalMigrationOutcome.Move, previewMove.Outcome);
        Assert.Equal(userId, previewMove.TargetUserId);
        Assert.False(previewMove.RiskNote); // the local-password link counts as non-AD enabled
        var previewSkip = Assert.Single(preview, row =>
            row.RoleId == roleId && row.Principal == @"CONTOSO\tc-no-link");
        Assert.Equal(AdRolePrincipalMigrationOutcome.Skipped, previewSkip.Outcome);

        var report = await fixture.CreateRepository().ExecuteAsync(CancellationToken.None);

        var created = Assert.Single(report.Created, row => row.RoleId == roleId);
        Assert.Equal(@"CONTOSO\tc-preview", created.SourcePrincipal);
        Assert.Equal(userId, created.TargetUserId);
        Assert.Equal("TierC PreviewMatch User", created.TargetDisplayName);
        Assert.Contains(report.Skipped, row =>
            row.RoleId == roleId
            && row.Principal == @"CONTOSO\tc-no-link"
            && row.Reason == AdRolePrincipalMigrationSkipReason.NoEnabledAdLink);
    }

    [Fact]
    public async Task Execute_CreatesOmpUserRows_AndRetainsAdSourceRows()
    {
        var userId = await fixture.InsertUserAsync("TierC Create User", active: true);
        await fixture.InsertAuthLinkAsync(userId, "AD", "S-1-5-21-11-22-33-1001");
        var roleId = await fixture.InsertRoleAsync("TierC Create Role");
        await fixture.InsertRolePrincipalAsync(roleId, "ADUser", "S-1-5-21-11-22-33-1001");

        var report = await fixture.CreateRepository().ExecuteAsync(CancellationToken.None);

        Assert.Single(report.Created, row => row.RoleId == roleId && row.TargetUserId == userId);

        // The new OmpUser assignment exists AND the AD source row is retained.
        var userIdPrincipal = userId.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(1, await fixture.CountRolePrincipalsAsync(roleId, "OmpUser", userIdPrincipal));
        Assert.Equal(1, await fixture.CountRolePrincipalsAsync(roleId, "ADUser", "S-1-5-21-11-22-33-1001"));
    }

    [Fact]
    public async Task Execute_RunTwice_CreatesNoRowsTheSecondTime()
    {
        var userId = await fixture.InsertUserAsync("TierC Idempotent User", active: true);
        await fixture.InsertAuthLinkAsync(userId, "AD", @"CONTOSO\tc-idem");
        var roleId = await fixture.InsertRoleAsync("TierC Idempotent Role");
        await fixture.InsertRolePrincipalAsync(roleId, "ADUser", @"CONTOSO\tc-idem");

        var first = await fixture.CreateRepository().ExecuteAsync(CancellationToken.None);
        var second = await fixture.CreateRepository().ExecuteAsync(CancellationToken.None);

        Assert.Single(first.Created, row => row.RoleId == roleId);
        Assert.DoesNotContain(second.Created, row => row.RoleId == roleId);
        Assert.True(second.AlreadyPresentCount >= 1);

        var userIdPrincipal = userId.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(1, await fixture.CountRolePrincipalsAsync(roleId, "OmpUser", userIdPrincipal));
    }

    [Fact]
    public async Task Execute_LeavesAdGroupRowsUntouched()
    {
        var roleId = await fixture.InsertRoleAsync("TierC Group Role");
        await fixture.InsertRolePrincipalAsync(roleId, "ADGroup", "S-1-5-21-11-22-33-1002");

        var preview = await fixture.CreateRepository().PreviewAsync(CancellationToken.None);
        Assert.DoesNotContain(preview, row => row.RoleId == roleId);

        var report = await fixture.CreateRepository().ExecuteAsync(CancellationToken.None);
        Assert.DoesNotContain(report.Created, row => row.RoleId == roleId);
        Assert.DoesNotContain(report.Skipped, row => row.RoleId == roleId);

        Assert.Equal(1, await fixture.CountRolePrincipalsAsync(roleId, "ADGroup", "S-1-5-21-11-22-33-1002"));
        Assert.Equal(1, await CountRolePrincipalsForRoleAsync(roleId));
    }

    [Fact]
    public async Task Fixture_EnforcesProductionProviderKeyUniqueness()
    {
        // The fixture must not be looser than production: an identical
        // (provider_id, provider_user_key) pair is rejected by
        // UQ_omp_user_auth_provider_key, so a linked-user ambiguity can only
        // arise through letter case, exactly as in production.
        var userId = await fixture.InsertUserAsync("TierC Unique User", active: true);
        await fixture.InsertAuthLinkAsync(userId, "AD", @"CONTOSO\tc-unique");

        var ex = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(
            () => fixture.InsertAuthLinkAsync(userId, "AD", @"CONTOSO\tc-unique"));
        Assert.Contains(ex.Number, new[] { 2601, 2627 });
    }

    [Fact]
    public async Task Execute_ReportsRowsWithoutLinkAsSkipped_AndAmbiguousAsSkipped()
    {
        // Production uniqueness is on (provider_id, provider_user_hash), a SHA-256
        // over the raw key, so an ambiguity arises through letter case: two active
        // AD links whose keys differ only in case both match the role principal
        // under the case-insensitive collation. The fixture now enforces the
        // production constraint, so the ambiguous pair is modeled exactly that way.
        var sharedUserA = await fixture.InsertUserAsync("TierC Ambiguous A", active: true);
        var sharedUserB = await fixture.InsertUserAsync("TierC Ambiguous B", active: true);
        await fixture.InsertAuthLinkAsync(sharedUserA, "AD", @"CONTOSO\tc-ambig");
        await fixture.InsertAuthLinkAsync(sharedUserB, "AD", @"CONTOSO\TC-AMBIG");
        var inactiveUser = await fixture.InsertUserAsync("TierC Inactive User", active: false);
        await fixture.InsertAuthLinkAsync(inactiveUser, "AD", @"CONTOSO\tc-inactive");
        var disabledLinkUser = await fixture.InsertUserAsync("TierC Disabled Link User", active: true);
        await fixture.InsertAuthLinkAsync(disabledLinkUser, "AD", @"CONTOSO\tc-disabled", authStatus: "disabled");

        var roleId = await fixture.InsertRoleAsync("TierC Skip Role");
        await fixture.InsertRolePrincipalAsync(roleId, "ADUser", @"CONTOSO\tc-missing");
        await fixture.InsertRolePrincipalAsync(roleId, "ADUser", @"CONTOSO\tc-ambig");
        await fixture.InsertRolePrincipalAsync(roleId, "ADUser", @"CONTOSO\tc-inactive");
        await fixture.InsertRolePrincipalAsync(roleId, "ADUser", @"CONTOSO\tc-disabled");

        var preview = await fixture.CreateRepository().PreviewAsync(CancellationToken.None);
        var previewRows = preview.Where(row => row.RoleId == roleId).ToList();
        Assert.Equal(4, previewRows.Count);
        Assert.All(previewRows, row => Assert.Equal(AdRolePrincipalMigrationOutcome.Skipped, row.Outcome));
        Assert.Equal(
            AdRolePrincipalMigrationSkipReason.NoEnabledAdLink,
            Assert.Single(previewRows, row => row.Principal == @"CONTOSO\tc-missing").SkipReason);
        Assert.Equal(
            AdRolePrincipalMigrationSkipReason.AmbiguousLinkedUsers,
            Assert.Single(previewRows, row => row.Principal == @"CONTOSO\tc-ambig").SkipReason);
        Assert.Equal(
            AdRolePrincipalMigrationSkipReason.LinkedUserInactive,
            Assert.Single(previewRows, row => row.Principal == @"CONTOSO\tc-inactive").SkipReason);
        Assert.Equal(
            AdRolePrincipalMigrationSkipReason.NoEnabledAdLink,
            Assert.Single(previewRows, row => row.Principal == @"CONTOSO\tc-disabled").SkipReason);

        var report = await fixture.CreateRepository().ExecuteAsync(CancellationToken.None);
        Assert.DoesNotContain(report.Created, row => row.RoleId == roleId);
        Assert.Equal(4, report.Skipped.Count(row => row.RoleId == roleId));
        Assert.Equal(4, await CountRolePrincipalsForRoleAsync(roleId));
    }

    private async Task<int> CountRolePrincipalsForRoleAsync(int roleId)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT COUNT(1) FROM omp.RolePrincipals WHERE RoleId = @role_id;",
            conn);
        cmd.Parameters.AddWithValue("@role_id", roleId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
