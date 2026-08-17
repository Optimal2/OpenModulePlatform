using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Database-backed tests for R7-F11 (the linked-user lookup must select only
/// active accounts instead of relying on an active-first sort order) and
/// R7-F15 (local password sign-in must run the same hash verification for an
/// unknown user name as for a wrong password, so the response time does not
/// reveal which accounts exist).
/// </summary>
public sealed class AuthResolutionDatabaseTests(AuthResolutionTestFixture fixture)
    : IClassFixture<AuthResolutionTestFixture>
{
    [Fact]
    public async Task ResolveLocalPasswordAsync_WhenUserDoesNotExist_StillRunsHashVerification()
    {
        // R7-F15. Before the fix a missing account returned before
        // LocalPasswordHasher.Verify ran, so an unknown user name answered
        // measurably faster than a wrong password. The fix verifies against a
        // dummy hash instead; this test pins that the verification is invoked.
        var countingHasher = new CountingLocalPasswordHasher(new OmpLocalPasswordHasher(new LocalPasswordHasher()));

        var result = await fixture.CreateAuthRepository(countingHasher)
            .ResolveLocalPasswordAsync("no-such-user-f15", "any-password-1", CancellationToken.None);

        Assert.Null(result.User);
        Assert.Equal("The user name or password is incorrect.", result.Error);
        Assert.Equal(1, countingHasher.VerifyCount);
    }

    [Fact]
    public async Task ResolveLocalPasswordAsync_WhenPasswordWrong_RunsHashVerificationOnce()
    {
        var userId = await fixture.InsertUserAsync("f15-wrong-password", active: true);
        await fixture.InsertLocalPasswordAsync("f15-wrong-password", "correct-password-1");
        await fixture.InsertAuthLinkAsync(userId, LocalPasswordIdentity.ProviderDisplayName, "f15-wrong-password");
        var countingHasher = new CountingLocalPasswordHasher(new OmpLocalPasswordHasher(new LocalPasswordHasher()));

        var result = await fixture.CreateAuthRepository(countingHasher)
            .ResolveLocalPasswordAsync("f15-wrong-password", "wrong-password-1", CancellationToken.None);

        Assert.Null(result.User);
        Assert.Equal("The user name or password is incorrect.", result.Error);
        Assert.Equal(1, countingHasher.VerifyCount);
    }

    [Fact]
    public async Task ResolveLocalPasswordAsync_WhenOnlyLinkTargetsDisabledUser_DeniesSignIn()
    {
        // R7-F11. The disabled-account guard must survive moving from the sort
        // order into the selection: an account whose only enabled link points
        // to a disabled OMP user stays blocked, with the distinct disabled
        // error preserved.
        var userId = await fixture.InsertUserAsync("f11-disabled-user", active: false);
        await fixture.InsertLocalPasswordAsync("f11-disabled-user", "valid-password-1");
        await fixture.InsertAuthLinkAsync(userId, LocalPasswordIdentity.ProviderDisplayName, "f11-disabled-user");

        var result = await fixture.CreateAuthRepository()
            .ResolveLocalPasswordAsync("f11-disabled-user", "valid-password-1", CancellationToken.None);

        Assert.Null(result.User);
        Assert.Equal("The linked OMP user is disabled.", result.Error);
    }

    [Fact]
    public async Task ResolveLocalPasswordAsync_WhenActiveAndDisabledLinksMatch_ResolvesActiveUser()
    {
        // R7-F11. Two enabled links match the lookup keys: the plain user name
        // points to an active user, the "name:" alias to a disabled one. The
        // active user must win regardless of row order.
        var activeUserId = await fixture.InsertUserAsync("f11-active-user", active: true);
        var disabledUserId = await fixture.InsertUserAsync("f11-shadow-disabled", active: false);
        await fixture.InsertLocalPasswordAsync("f11-multi-link", "valid-password-1");
        await fixture.InsertAuthLinkAsync(activeUserId, LocalPasswordIdentity.ProviderDisplayName, "f11-multi-link");
        await fixture.InsertAuthLinkAsync(disabledUserId, LocalPasswordIdentity.ProviderDisplayName, "name:f11-multi-link");

        var result = await fixture.CreateAuthRepository()
            .ResolveLocalPasswordAsync("f11-multi-link", "valid-password-1", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.User);
        Assert.Equal(activeUserId, result.User.UserId);
    }

    [Fact]
    public async Task CreateLocalPasswordUserAsync_WhenNameHeldByDisabledUsersLink_DeniesRegistration()
    {
        // R7-F11. The registration uniqueness check must keep treating an
        // enabled auth link to a disabled user as "name in use"; otherwise a
        // re-registered name would shadow the deliberately disabled account.
        await fixture.SetSelfRegistrationValueAsync("true");
        var disabledUserId = await fixture.InsertUserAsync("f11-taken-name", active: false);
        await fixture.InsertAuthLinkAsync(disabledUserId, LocalPasswordIdentity.ProviderDisplayName, "f11-taken-name");

        var result = await fixture.CreateAuthRepository()
            .CreateLocalPasswordUserAsync("f11-taken-name", "valid-password-1", CancellationToken.None);

        Assert.Null(result.User);
        Assert.Equal("User name is already in use.", result.Error);
    }

    private sealed class CountingLocalPasswordHasher(IOmpLocalPasswordHasher inner) : IOmpLocalPasswordHasher
    {
        public int VerifyCount { get; private set; }

        public string Hash(string password)
            => inner.Hash(password);

        public bool Verify(string password, string storedHash)
        {
            VerifyCount++;
            return inner.Verify(password, storedHash);
        }
    }
}
