using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Database-backed tests for R7-F11 (the linked-user lookup must select only
/// active accounts instead of relying on an active-first sort order) and
/// R7-F15 (local password sign-in must run the same hash verification for an
/// unknown user name as for a wrong password, so the response time does not
/// reveal which accounts exist). R7-F12 adds the canonical user-name rule for
/// omp.auth_provider_lpwd (one shared normalization, comparisons pinned to a
/// binary collation, plus the core-setup migration for legacy rows) and
/// R7-F16 the infrastructure-error flag that keeps non-credential failures
/// out of the lockout budget.
/// </summary>
public sealed class AuthResolutionDatabaseTests(AuthResolutionTestFixture fixture)
    : IClassFixture<AuthResolutionTestFixture>
{
    [Fact]
    public async Task ResolveOidcAsync_KeepsOnlyRoleMappedGroupsInTheSignInPrincipals()
    {
        // Campaign adfs-grupper-bara-rollmatchade-i-kakan. The unit test on
        // BuildOidcRolePrincipals pins the overload; this pins the wiring: the sign-in path
        // must hand it the role-mapped subset, not every group claim. Before the fix every
        // group became an ADGroup claim in the cookie and a directory user with a few hundred
        // groups exceeded the web server's request-header limit after signing in.
        await fixture.InsertRolePrincipalAsync("adfs-group-filter-role", "ADGroup", @"CONTOSO\archive-writers");

        var claims = new OmpOidcResolvedClaims
        {
            ProviderName = "ADFS",
            Subject = "adfs-group-filter-subject",
            Issuer = "https://idp.example.invalid/adfs",
            ProviderUserKey = "adfs-group-filter-subject",
            ProviderUserKeyCandidates = ["adfs-group-filter-subject"],
            UserName = @"CONTOSO\group-filter-user",
            DisplayName = "Group Filter User",
            UserPrincipalCandidates = [@"CONTOSO\group-filter-user"],
            Groups = [@"CONTOSO\archive-writers", @"CONTOSO\everyone", @"CONTOSO\printer-users"]
        };

        var user = await fixture.CreateAuthRepository().ResolveOidcAsync(claims, CancellationToken.None);

        Assert.NotNull(user);
        Assert.Equal(
            [@"CONTOSO\archive-writers"],
            user.RolePrincipals.Where(p => p.PrincipalType == "ADGroup").Select(p => p.Principal).ToList());
        Assert.Contains(("ADUser", @"CONTOSO\group-filter-user"), user.RolePrincipals);
    }

    [Theory]
    [InlineData("AutoIfAuthenticated", "CONTOSO")]
    [InlineData("AutoIfAuthenticated", "")]
    [InlineData("AutoIfRole", "CONTOSO")]
    public async Task ResolveOidcAsync_FirstSignInWithoutOmpUser_IsProvisionedLikeWindows(string mode, string allowedDomains)
    {
        // Operator report 2026-09-07: a first ADFS sign-in without an OMP account looked like
        // an error, while a first Windows sign-in was provisioned. This drives the OIDC path
        // with the claim shape a real AD FS delivers (UPN as user name, unique_name as the
        // DOMAIN\name candidate, no objectsid, bare group names) under both automatic modes.
        var suffix = mode + (allowedDomains.Length == 0 ? "-any" : "-listed");
        await fixture.SetGlobalSettingAsync(OmpAuthDefaults.ConfigurationCategory, OmpAuthDefaults.ExternalUserProvisioningModeSetting, mode);
        await fixture.SetGlobalSettingAsync(OmpRbacDefaults.ConfigurationCategory, OmpRbacDefaults.AuthenticatedUsersWindowsDomainsSetting, allowedDomains);
        await fixture.InsertRolePrincipalAsync("first-signin-role-" + suffix, "ADGroup", "first-signin-readers-" + suffix);

        var claims = new OmpOidcResolvedClaims
        {
            ProviderName = "ADFS",
            Subject = "pairwise-first-" + suffix,
            Issuer = "https://idp.example.invalid/adfs",
            ProviderUserKey = "pairwise-first-" + suffix,
            ProviderUserKeyCandidates = ["pairwise-first-" + suffix, "sub:pairwise-first-" + suffix, "upn:first." + suffix + "@contoso.example"],
            UserName = "first." + suffix + "@contoso.example",
            DisplayName = "First " + suffix,
            UserPrincipalCandidates = [@"CONTOSO\first." + suffix, "first." + suffix + "@contoso.example"],
            Groups = ["first-signin-readers-" + suffix, "unrelated-group"]
        };

        var user = await fixture.CreateAuthRepository().ResolveOidcAsync(claims, CancellationToken.None);

        Assert.NotNull(user);
        Assert.True(user.UserId.HasValue, "the first OIDC sign-in must create an OMP user like the Windows path does");
        Assert.Contains(user.RolePrincipals, p => p.PrincipalType == "OmpUser");
        Assert.Equal("First " + suffix, user.DisplayName);

        // The second sign-in resolves the same user through the stored auth link.
        var again = await fixture.CreateAuthRepository().ResolveOidcAsync(claims, CancellationToken.None);
        Assert.Equal(user.UserId, again?.UserId);
    }

    [Fact]
    public async Task ResolveOidcAsync_MatchesRoleRowsInEitherGroupForm()
    {
        // Campaign ad-grupper-samma-form-windows-och-adfs. Windows sign-in delivers
        // DOMAIN\Group, an OIDC provider may deliver the bare Group; a role row written in
        // either form must match from both paths when the domain is allowlisted.
        await fixture.SetGlobalSettingAsync(
            OmpRbacDefaults.ConfigurationCategory, OmpRbacDefaults.AuthenticatedUsersWindowsDomainsSetting, "CONTOSO");
        await fixture.InsertRolePrincipalAsync("group-form-qualified-role", "ADGroup", @"CONTOSO\form-qualified");
        await fixture.InsertRolePrincipalAsync("group-form-bare-role", "ADGroup", "form-bare");
        await fixture.InsertRolePrincipalAsync("group-form-foreign-role", "ADGroup", "form-foreign");

        var claims = new OmpOidcResolvedClaims
        {
            ProviderName = "ADFS",
            Subject = "group-form-subject",
            Issuer = "https://idp.example.invalid/adfs",
            ProviderUserKey = "group-form-subject",
            ProviderUserKeyCandidates = ["group-form-subject"],
            UserName = @"CONTOSO\group-form-user",
            DisplayName = "Group Form User",
            UserPrincipalCandidates = [@"CONTOSO\group-form-user"],
            // Bare form as an IdP sends it, qualified form as Windows delivers it, and a
            // qualified group from a domain outside the allowlist that must not be stripped.
            Groups = ["form-qualified", @"CONTOSO\form-bare", @"OTHERDOM\form-foreign"]
        };

        var user = await fixture.CreateAuthRepository().ResolveOidcAsync(claims, CancellationToken.None);

        Assert.NotNull(user);
        Assert.Equal(
            [@"CONTOSO\form-qualified", "form-bare"],
            user.RolePrincipals.Where(p => p.PrincipalType == "ADGroup").Select(p => p.Principal).OrderBy(p => p).ToList());
    }

    [Fact]
    public async Task ResolveLocalPasswordAsync_WhenStoredNameIsNotCanonical_DoesNotMatch()
    {
        // R7-F12. A legacy row written before the shared normalization rule
        // (or by hand) holds a different casing. The hash lookup is pinned to
        // the canonical form with a binary collation, so the row must not
        // match: on a case-insensitive database collation it otherwise would --
        // possibly the wrong row, if case-variant duplicates ever coexisted.
        // The auth link below is canonical on purpose: had the hash lookup
        // matched, sign-in would have succeeded end to end.
        var userId = await fixture.InsertUserAsync("f12-legacy-case", active: true);
        await fixture.InsertLocalPasswordAsync("F12-Legacy-Case@Example.com", "valid-password-1");
        await fixture.InsertAuthLinkAsync(userId, LocalPasswordIdentity.ProviderDisplayName, "f12-legacy-case@example.com");

        var result = await fixture.CreateAuthRepository()
            .ResolveLocalPasswordAsync("f12-legacy-case@example.com", "valid-password-1", CancellationToken.None);

        Assert.Null(result.User);
        Assert.Equal("The user name or password is incorrect.", result.Error);
    }

    [Fact]
    public async Task CoreSetupMigration_FoldsLegacyCasing_AndRestoresSignIn()
    {
        // R7-F12. The idempotent migration shipped in the core setup script
        // folds non-canonical user names -- and their lpwd auth links -- to
        // the canonical form; afterwards sign-in with the canonical name
        // succeeds against the exact binary-pinned lookup.
        var userId = await fixture.InsertUserAsync("f12-migrated-user", active: true);
        await fixture.InsertLocalPasswordAsync("F12-Migrated-User@Example.com", "valid-password-1");
        await fixture.InsertAuthLinkAsync(userId, LocalPasswordIdentity.ProviderDisplayName, "F12-Migrated-User@Example.com");

        await fixture.RunLocalPasswordCanonicalizationMigrationAsync();

        var result = await fixture.CreateAuthRepository()
            .ResolveLocalPasswordAsync("f12-migrated-user@example.com", "valid-password-1", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.User);
        Assert.Equal(userId, result.User.UserId);
    }

    [Fact]
    public async Task LocalPasswordSignIn_WriteAndReadShareTheCanonicalRule()
    {
        // R7-F12. Registration and sign-in must apply one and the same
        // normalization rule: casing and surrounding whitespace in the input
        // never become part of the stored key.
        await fixture.SetSelfRegistrationValueAsync("true");

        var created = await fixture.CreateAuthRepository()
            .CreateLocalPasswordUserAsync("  F12-RoundTrip@Example.com ", "valid-password-1", CancellationToken.None);
        Assert.NotNull(created.User);

        var result = await fixture.CreateAuthRepository()
            .ResolveLocalPasswordAsync("F12-ROUNDTRIP@example.COM", "valid-password-1", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.User);
        Assert.Equal(created.User.UserId, result.User.UserId);
    }

    [Fact]
    public async Task ResolveLocalPasswordAsync_WhenProviderDisabled_FlagsInfrastructureError()
    {
        // R7-F16. A disabled or missing local password provider is an
        // infrastructure/configuration condition -- no credential was ever
        // compared -- so the sign-in path must not count it toward the
        // lockout budget that bounds password guessing (R5-F8).
        await fixture.SetProviderEnabledAsync(LocalPasswordIdentity.ProviderDisplayName, false);
        try
        {
            var result = await fixture.CreateAuthRepository()
                .ResolveLocalPasswordAsync("f16-infra-user", "any-password-1", CancellationToken.None);

            Assert.Null(result.User);
            Assert.Equal("Local password sign-in is disabled.", result.Error);
            Assert.True(result.IsInfrastructureError);
        }
        finally
        {
            await fixture.SetProviderEnabledAsync(LocalPasswordIdentity.ProviderDisplayName, true);
        }
    }

    [Fact]
    public async Task ResolveLocalPasswordAsync_WhenPasswordWrong_IsNotInfrastructureError()
    {
        // R7-F16. A genuine bad-credential attempt must keep counting toward
        // the lockout budget; only infrastructure faults are exempt.
        var userId = await fixture.InsertUserAsync("f16-wrong-password", active: true);
        await fixture.InsertLocalPasswordAsync("f16-wrong-password", "correct-password-1");
        await fixture.InsertAuthLinkAsync(userId, LocalPasswordIdentity.ProviderDisplayName, "f16-wrong-password");

        var result = await fixture.CreateAuthRepository()
            .ResolveLocalPasswordAsync("f16-wrong-password", "wrong-password-1", CancellationToken.None);

        Assert.Null(result.User);
        Assert.Equal("The user name or password is incorrect.", result.Error);
        Assert.False(result.IsInfrastructureError);
    }

    [Fact]
    public async Task CreateLocalPasswordUserAsync_WhenProviderDisabled_FlagsInfrastructureError()
    {
        // R7-F16. The registration path applies the same rule: a disabled
        // provider is not a failed attempt by the caller.
        await fixture.SetSelfRegistrationValueAsync("true");
        await fixture.SetProviderEnabledAsync(LocalPasswordIdentity.ProviderDisplayName, false);
        try
        {
            var result = await fixture.CreateAuthRepository()
                .CreateLocalPasswordUserAsync("f16-register-user", "valid-password-1", CancellationToken.None);

            Assert.Null(result.User);
            Assert.Equal("Local password sign-in is disabled.", result.Error);
            Assert.True(result.IsInfrastructureError);
        }
        finally
        {
            await fixture.SetProviderEnabledAsync(LocalPasswordIdentity.ProviderDisplayName, true);
        }
    }

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
