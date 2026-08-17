using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// R7-F10. The revocation decision and the validation hook's behavior around
/// it: a session is rejected when the account was disabled, when the security
/// stamp rotated (password changed), when the account is gone, or -- in strict
/// mode -- when the state cannot be read at all.
/// </summary>
public sealed class OmpSessionRevocationValidatorTests
{
    private static readonly Guid Stamp = Guid.NewGuid();

    [Fact]
    public async Task ValidateAsync_ActiveAccountWithMatchingStamp_KeepsSession()
    {
        var store = new FakeStore(new OmpSessionAccountState(1, Stamp));
        var context = CreateContext(userId: 42, stamp: Stamp.ToString());

        await CreateValidator(store).ValidateAsync(context);

        Assert.True(context.Principal?.Identity?.IsAuthenticated);
        Assert.Equal(1, store.AccountStateReads);
    }

    [Fact]
    public async Task ValidateAsync_AccountDisabledAfterSignIn_NextRequestIsRejected()
    {
        // The finding's scenario: the user signs in, the account is disabled,
        // and the very next request must be denied. A zero cache window makes
        // every request re-check, which is what "immediately" means here.
        var store = new FakeStore(new OmpSessionAccountState(1, Stamp))
        {
            Settings = new OmpSessionRevocationSettings(Strict: true, CacheSeconds: 0)
        };
        var validator = CreateValidator(store);
        var signedIn = CreateContext(userId: 42, stamp: Stamp.ToString());

        await validator.ValidateAsync(signedIn);
        Assert.True(signedIn.Principal?.Identity?.IsAuthenticated);

        store.State = new OmpSessionAccountState(2, Guid.NewGuid());
        var nextRequest = CreateContext(userId: 42, stamp: Stamp.ToString());

        await validator.ValidateAsync(nextRequest);

        Assert.Null(nextRequest.Principal);
    }

    [Fact]
    public async Task ValidateAsync_StampRotatedByPasswordChange_NextRequestIsRejected()
    {
        var store = new FakeStore(new OmpSessionAccountState(1, Stamp))
        {
            Settings = new OmpSessionRevocationSettings(Strict: true, CacheSeconds: 0)
        };
        var validator = CreateValidator(store);
        var signedIn = CreateContext(userId: 42, stamp: Stamp.ToString());

        await validator.ValidateAsync(signedIn);
        Assert.True(signedIn.Principal?.Identity?.IsAuthenticated);

        // The account is still active, but the password change rotated the
        // stamp -- the cookie signed in with the old credentials must die.
        store.State = new OmpSessionAccountState(1, Guid.NewGuid());
        var nextRequest = CreateContext(userId: 42, stamp: Stamp.ToString());

        await validator.ValidateAsync(nextRequest);

        Assert.Null(nextRequest.Principal);
    }

    [Fact]
    public async Task ValidateAsync_WithinCacheWindow_ServesCachedStateWithoutRequery()
    {
        // The documented trade-off: inside the cache window a revocation has
        // not propagated yet, and the store is not hit again.
        var store = new FakeStore(new OmpSessionAccountState(1, Stamp))
        {
            Settings = new OmpSessionRevocationSettings(Strict: true, CacheSeconds: 60)
        };
        var validator = CreateValidator(store);

        await validator.ValidateAsync(CreateContext(userId: 42, stamp: Stamp.ToString()));
        store.State = new OmpSessionAccountState(2, Guid.NewGuid());
        var withinWindow = CreateContext(userId: 42, stamp: Stamp.ToString());

        await validator.ValidateAsync(withinWindow);

        Assert.Equal(1, store.AccountStateReads);
        Assert.True(withinWindow.Principal?.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task ValidateAsync_AccountNoLongerExists_RejectsSession()
    {
        var store = new FakeStore(null);
        var context = CreateContext(userId: 42, stamp: Stamp.ToString());

        await CreateValidator(store).ValidateAsync(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task ValidateAsync_SessionWithoutStampClaim_RejectsSession()
    {
        // Cookies issued before the security stamp existed carry no claim; they
        // are signed out once at upgrade instead of living on unverifiable.
        var store = new FakeStore(new OmpSessionAccountState(1, Stamp));
        var context = CreateContext(userId: 42, stamp: null);

        await CreateValidator(store).ValidateAsync(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task ValidateAsync_StoreFailureInStrictMode_RejectsSession()
    {
        var store = new FakeStore(null) { ThrowOnRead = true };
        var context = CreateContext(userId: 42, stamp: Stamp.ToString());

        await CreateValidator(store).ValidateAsync(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task ValidateAsync_StoreFailureInLenientMode_KeepsSession()
    {
        var store = new FakeStore(null)
        {
            ThrowOnRead = true,
            Settings = new OmpSessionRevocationSettings(Strict: false, CacheSeconds: 60)
        };
        var context = CreateContext(userId: 42, stamp: Stamp.ToString());

        await CreateValidator(store).ValidateAsync(context);

        Assert.True(context.Principal?.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task ValidateAsync_StoreFailure_IsNotCached()
    {
        var store = new FakeStore(new OmpSessionAccountState(1, Stamp)) { ThrowOnRead = true };
        var validator = CreateValidator(store);

        await validator.ValidateAsync(CreateContext(userId: 42, stamp: Stamp.ToString()));

        store.ThrowOnRead = false;
        var retry = CreateContext(userId: 42, stamp: Stamp.ToString());

        await validator.ValidateAsync(retry);

        Assert.Equal(2, store.AccountStateReads);
        Assert.True(retry.Principal?.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task ValidateAsync_IdentityWithoutOmpUserId_IsNotChecked()
    {
        var store = new FakeStore(null);
        var context = CreateContext(userId: null, stamp: null);

        await CreateValidator(store).ValidateAsync(context);

        Assert.Equal(0, store.AccountStateReads);
        Assert.True(context.Principal?.Identity?.IsAuthenticated);
    }

    [Theory]
    [InlineData(1, "stamp-match-keeps")]
    [InlineData(2, "disabled-rejects")]
    public void ShouldReject_DecisionMatrix(int accountStatus, string expectation)
    {
        var state = new OmpSessionAccountState(accountStatus, Stamp);

        var rejected = OmpSessionRevocationValidator.ShouldReject(state, Stamp.ToString(), out _);

        Assert.Equal(expectation == "disabled-rejects", rejected);
    }

    [Fact]
    public void Settings_Parse_FailedReadsFailClosed()
    {
        var settings = OmpSessionRevocationSettings.Parse(
            new OmpConfigurationRead(null, Failed: true),
            new OmpConfigurationRead(null, Failed: true));

        Assert.True(settings.Strict);
        Assert.Equal(OmpSessionRevocationSettings.DefaultCacheSeconds, settings.CacheSeconds);
    }

    [Theory]
    [InlineData("lenient", false)]
    [InlineData("Lenient", false)]
    [InlineData("strict", true)]
    [InlineData("garbage", true)]
    [InlineData(null, true)]
    public void Settings_Parse_FailureMode(string? value, bool expectedStrict)
    {
        var settings = OmpSessionRevocationSettings.Parse(
            new OmpConfigurationRead(value, Failed: false),
            new OmpConfigurationRead(null, Failed: false));

        Assert.Equal(expectedStrict, settings.Strict);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("30", 30)]
    [InlineData("300", 300)]
    [InlineData("999", 300)]
    [InlineData("-5", 60)]
    [InlineData("garbage", 60)]
    [InlineData(null, 60)]
    public void Settings_Parse_CacheSeconds(string? value, int expectedSeconds)
    {
        var settings = OmpSessionRevocationSettings.Parse(
            new OmpConfigurationRead(null, Failed: false),
            new OmpConfigurationRead(value, Failed: false));

        Assert.Equal(expectedSeconds, settings.CacheSeconds);
    }

    private static OmpSessionRevocationValidator CreateValidator(FakeStore store)
        => new(
            store,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<OmpSessionRevocationValidator>.Instance);

    private static CookieValidatePrincipalContext CreateContext(int? userId, string? stamp)
    {
        var claims = new List<Claim>();
        if (userId is int id)
        {
            claims.Add(new Claim(OmpAuthDefaults.UserIdClaimType, id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (stamp is not null)
        {
            claims.Add(new Claim(OmpAuthDefaults.SecurityStampClaimType, stamp));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, OmpAuthDefaults.AuthenticationScheme));
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

    private sealed class FakeStore : IOmpSessionRevocationStore
    {
        public FakeStore(OmpSessionAccountState? state)
        {
            State = state;
        }

        public OmpSessionAccountState? State { get; set; }
        public OmpSessionRevocationSettings Settings { get; set; } = OmpSessionRevocationSettings.Default;
        public bool ThrowOnRead { get; set; }
        public int AccountStateReads { get; private set; }

        public Task<OmpSessionAccountState?> GetAccountStateAsync(int userId, CancellationToken ct)
        {
            AccountStateReads++;
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Simulated store failure.");
            }

            return Task.FromResult(State);
        }

        public Task<OmpSessionRevocationSettings> GetSettingsAsync(CancellationToken ct)
            => Task.FromResult(Settings);
    }
}
