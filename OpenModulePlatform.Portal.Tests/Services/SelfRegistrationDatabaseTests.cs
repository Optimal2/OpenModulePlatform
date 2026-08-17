namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// R7-F17, proven against a real database: the auth/selfRegistrationEnabled
/// flag actually gates account creation. Disabled means denied, enabled means
/// created, and -- the opt-in default the seed used to defeat -- an absent
/// value means denied too.
/// </summary>
public sealed class SelfRegistrationDatabaseTests(SelfRegistrationTestFixture fixture)
    : IClassFixture<SelfRegistrationTestFixture>
{
    [Fact]
    public async Task CreateLocalPasswordUserAsync_WhenFlagDisabled_DeniesRegistration()
    {
        await fixture.SetSelfRegistrationValueAsync("false");

        var result = await fixture.CreateAuthRepository()
            .CreateLocalPasswordUserAsync("selfreg-disabled-1", "valid-password-1", CancellationToken.None);

        Assert.Null(result.User);
        Assert.Equal("Account registration is disabled.", result.Error);
        Assert.False(await fixture.LocalPasswordUserExistsAsync("selfreg-disabled-1"));
    }

    [Fact]
    public async Task CreateLocalPasswordUserAsync_WhenFlagEnabled_CreatesUser()
    {
        await fixture.SetSelfRegistrationValueAsync("true");

        var result = await fixture.CreateAuthRepository()
            .CreateLocalPasswordUserAsync("selfreg-enabled-1", "valid-password-1", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.User);
        Assert.True(await fixture.LocalPasswordUserExistsAsync("selfreg-enabled-1"));
    }

    [Fact]
    public async Task CreateLocalPasswordUserAsync_WhenFlagAbsent_DeniesRegistration()
    {
        // Opt-in (R3-F2/R7-F17): an installation without the seeded row has
        // self-registration OFF. Before the fix this arrangement created the
        // account, because the repository read an absent value as enabled.
        await fixture.SetSelfRegistrationValueAsync(null);

        var result = await fixture.CreateAuthRepository()
            .CreateLocalPasswordUserAsync("selfreg-absent-1", "valid-password-1", CancellationToken.None);

        Assert.Null(result.User);
        Assert.Equal("Account registration is disabled.", result.Error);
        Assert.False(await fixture.LocalPasswordUserExistsAsync("selfreg-absent-1"));
    }
}
