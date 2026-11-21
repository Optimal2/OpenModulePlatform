namespace OpenModulePlatform.Portal.Tests.Services;

[Collection(AdPrincipalMigrationCollection.CollectionName)]
public sealed class BuiltInRoleProtectionTests(AdPrincipalMigrationTestFixture fixture)
{
    [Fact]
    public async Task BuiltInRoles_CannotBeDeletedRenamedOrLeftWithoutPrincipals()
    {
        var repository = fixture.CreateRbacAdminRepository();
        foreach (var roleName in new[] { "PortalAdmins", "Everyone", "AuthenticatedUsers" })
        {
            var roleId = await fixture.InsertRoleAsync(roleName);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteRoleAsync(roleId, CancellationToken.None));
            Assert.True(await fixture.RoleExistsAsync(roleId));
        }

        var portalAdmins = await fixture.GetRoleIdAsync("PortalAdmins");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveRoleAsync(
            new() { RoleId = portalAdmins, Name = "RenamedAdmins" },
            CancellationToken.None));

        await fixture.InsertRolePrincipalAsync(portalAdmins, "OmpUser", "1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RemovePrincipalFromRoleAsync(
            portalAdmins,
            "OmpUser",
            "1",
            CancellationToken.None));
        Assert.Equal(1, await fixture.CountRolePrincipalsAsync(portalAdmins, "OmpUser", "1"));
    }
}
