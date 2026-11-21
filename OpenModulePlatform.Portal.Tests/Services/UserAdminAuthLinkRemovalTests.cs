using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

[Collection(AdPrincipalMigrationCollection.CollectionName)]
public sealed class UserAdminAuthLinkRemovalTests(AdPrincipalMigrationTestFixture fixture)
{
    [Fact]
    public async Task RemoveAuthLink_LastEnabledLink_IsRefused()
    {
        var userId = await fixture.InsertUserAsync("Last sign-in link", active: true);
        await fixture.InsertAuthLinkAsync(userId, "AD", @"CONTOSO\last-link");
        var authLinkId = (await fixture.GetAuthLinkIdsAsync(userId)).Single();

        var result = await fixture.CreateUserAdminRepository()
            .RemoveAuthLinkAsync(userId, authLinkId, CancellationToken.None);

        Assert.Equal(RemoveAuthLinkStatus.LastAuthLink, result.Status);
        Assert.Single(await fixture.GetAuthLinkIdsAsync(userId));
    }

    [Fact]
    public async Task RemoveAuthLink_WithAnotherEnabledLink_RemovesLinkAndRotatesSecurityStamp()
    {
        var userId = await fixture.InsertUserAsync("One of two sign-in links", active: true);
        await fixture.InsertAuthLinkAsync(userId, "AD", @"CONTOSO\first-link");
        await fixture.InsertAuthLinkAsync(userId, "ADFS", "second-link");
        var firstAuthLinkId = (await fixture.GetAuthLinkIdsAsync(userId)).First();
        var originalStamp = await fixture.GetSecurityStampAsync(userId);

        var result = await fixture.CreateUserAdminRepository()
            .RemoveAuthLinkAsync(userId, firstAuthLinkId, CancellationToken.None);

        Assert.Equal(RemoveAuthLinkStatus.Removed, result.Status);
        Assert.Single(await fixture.GetAuthLinkIdsAsync(userId));
        Assert.NotEqual(originalStamp, await fixture.GetSecurityStampAsync(userId));
    }
}
