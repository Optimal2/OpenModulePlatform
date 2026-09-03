using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// R7-F10, proven against a real database: disabling an account and resetting
/// its password rotate the security stamp, and the session validation hook
/// rejects the old cookie on the next request.
/// </summary>
public sealed class SessionRevocationDatabaseTests(SessionRevocationTestFixture fixture)
    : IClassFixture<SessionRevocationTestFixture>
{
    [Fact]
    public async Task ResetLocalPasswordAsync_RotatesSecurityStamp()
    {
        var userId = await fixture.CreateUserWithLocalLoginAsync("revocation-reset-1");
        var before = await fixture.ReadAccountStateAsync(userId);

        var result = await fixture.CreateAdminRepository()
            .ResetLocalPasswordAsync(userId, "new-password-123", CancellationToken.None);

        var after = await fixture.ReadAccountStateAsync(userId);
        Assert.Equal(ResetLocalPasswordResult.Reset, result);
        var beforeState = Assert.NotNull(before);
        var afterState = Assert.NotNull(after);
        Assert.NotEqual(beforeState.SecurityStamp, afterState.SecurityStamp);
    }

    [Fact]
    public async Task UpdateUserAsync_DisablingAccount_RotatesSecurityStamp()
    {
        var userId = await fixture.CreateUserWithLocalLoginAsync("revocation-disable-1");
        var before = await fixture.ReadAccountStateAsync(userId);

        var updated = await fixture.CreateAdminRepository().UpdateUserAsync(
            new OmpUserEditData
            {
                UserId = userId,
                DisplayName = "Disabled User",
                AccountStatus = 2
            },
            CancellationToken.None);

        var after = await fixture.ReadAccountStateAsync(userId);
        Assert.True(updated);
        var beforeState = Assert.NotNull(before);
        var afterState = Assert.NotNull(after);
        Assert.Equal(2, afterState.AccountStatus);
        Assert.NotEqual(beforeState.SecurityStamp, afterState.SecurityStamp);
    }

    [Fact]
    public async Task UpdateUserAsync_KeepingAccountActive_PreservesSecurityStamp()
    {
        // A plain display-name edit must not sign the user out.
        var userId = await fixture.CreateUserWithLocalLoginAsync("revocation-rename-1");
        var before = await fixture.ReadAccountStateAsync(userId);

        var updated = await fixture.CreateAdminRepository().UpdateUserAsync(
            new OmpUserEditData
            {
                UserId = userId,
                DisplayName = "Renamed User",
                AccountStatus = 1
            },
            CancellationToken.None);

        var after = await fixture.ReadAccountStateAsync(userId);
        Assert.True(updated);
        var beforeState = Assert.NotNull(before);
        var afterState = Assert.NotNull(after);
        Assert.Equal(beforeState.SecurityStamp, afterState.SecurityStamp);
    }

    [Fact]
    public async Task ValidateAsync_AfterAccountDisabledInDatabase_RejectsOldCookie()
    {
        // End to end with the production store and hook: sign in (stamp claim
        // from the account row), validate fine, disable the account, and the
        // next request is denied. A fresh cache stands in for the cache window
        // having elapsed -- the window only bounds how long the old answer is
        // served, it never changes the verdict.
        var userId = await fixture.CreateUserWithLocalLoginAsync("revocation-e2e-1");
        var signedInState = await fixture.ReadAccountStateAsync(userId);
        var signedInAccount = Assert.NotNull(signedInState);

        var store = fixture.CreateRevocationStore();
        var signedIn = CreateContext(userId, signedInAccount.SecurityStamp);
        await CreateValidator(store).ValidateAsync(signedIn);
        Assert.True(signedIn.Principal?.Identity?.IsAuthenticated);

        await fixture.CreateAdminRepository().UpdateUserAsync(
            new OmpUserEditData
            {
                UserId = userId,
                DisplayName = "Disabled User",
                AccountStatus = 2
            },
            CancellationToken.None);

        var nextRequest = CreateContext(userId, signedInAccount.SecurityStamp);
        await CreateValidator(store).ValidateAsync(nextRequest);

        Assert.Null(nextRequest.Principal);
    }

    private static OmpSessionRevocationValidator CreateValidator(OmpSqlSessionRevocationStore store)
        => new(
            store,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<OmpSessionRevocationValidator>.Instance);

    private static CookieValidatePrincipalContext CreateContext(int userId, Guid stamp)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(OmpAuthDefaults.UserIdClaimType, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim(OmpAuthDefaults.SecurityStampClaimType, stamp.ToString())
            ],
            OmpAuthDefaults.AuthenticationScheme));
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };

        return new CookieValidatePrincipalContext(
            httpContext,
            new AuthenticationScheme(OmpAuthDefaults.AuthenticationScheme, null, typeof(IAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(principal, OmpAuthDefaults.AuthenticationScheme));
    }
}
