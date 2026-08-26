using OpenModulePlatform.Auth.Models;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// Campaign ad-principalformen-hela-vagen-adfs-till-rbac, DEL 1: the ADFS/OIDC
/// sign-in path must build the DOMAIN\name principal form from more than one
/// claim mapping, so a single misconfigured claim type cannot strip every
/// AD-linked role. Measured on a customer ADFS (2026-08-21): sub, upn, sid and
/// unique_name arrive; samaccountname and netbiosname do not. All test data is
/// invented (CONTOSO\anna, S-1-5-21-11-22-33-1001).
/// </summary>
public sealed class OmpOidcPrincipalFormTests
{
    private const string UserSid = "S-1-5-21-11-22-33-1001";
    private const string GroupSid = "S-1-5-21-11-22-33-2001";

    [Fact]
    public void Resolve_UniqueNameClaim_BuildsDomainNamePrincipal()
    {
        var principal = CreatePrincipal(
            new Claim("sub", "pairwise-subject-1"),
            new Claim("unique_name", @"CONTOSO\anna"));

        var resolved = OmpOidcClaimResolver.Resolve(principal, BrokenSamAccountNameOptions());

        Assert.NotNull(resolved);
        Assert.Contains(@"CONTOSO\anna", resolved.UserPrincipalCandidates);
    }

    [Fact]
    public void Resolve_UniqueNameWsUriClaim_BuildsDomainNamePrincipal()
    {
        var principal = CreatePrincipal(
            new Claim("sub", "pairwise-subject-1"),
            new Claim(ClaimTypes.Name, @"CONTOSO\anna"));

        var resolved = OmpOidcClaimResolver.Resolve(principal, BrokenSamAccountNameOptions());

        Assert.NotNull(resolved);
        Assert.Contains(@"CONTOSO\anna", resolved.UserPrincipalCandidates);
    }

    [Fact]
    public void Resolve_WindowsAccountNameClaim_BuildsDomainNamePrincipal()
    {
        var principal = CreatePrincipal(
            new Claim("sub", "pairwise-subject-1"),
            new Claim("windowsaccountname", @"CONTOSO\anna"));

        var resolved = OmpOidcClaimResolver.Resolve(principal, BrokenSamAccountNameOptions());

        Assert.NotNull(resolved);
        Assert.Contains(@"CONTOSO\anna", resolved.UserPrincipalCandidates);
    }

    [Fact]
    public void Resolve_SidClaim_TranslatedToDomainNamePrincipal()
    {
        var translator = new FakeSidAccountTranslator();
        translator.SidToName[UserSid] = @"CONTOSO\anna";
        var principal = CreatePrincipal(
            new Claim("sub", "pairwise-subject-1"),
            new Claim("sid", UserSid));

        var resolved = OmpOidcClaimResolver.Resolve(
            principal, BrokenSamAccountNameOptions(), translator);

        Assert.NotNull(resolved);
        Assert.Contains(@"CONTOSO\anna", resolved.UserPrincipalCandidates);
        Assert.True(translator.SidCalls > 0);
    }

    [Fact]
    public void Resolve_SidTranslationFailure_FallsBackToOtherForms()
    {
        var translator = new FakeSidAccountTranslator(); // no mappings: every lookup misses
        var principal = CreatePrincipal(
            new Claim("sub", "pairwise-subject-1"),
            new Claim("sid", UserSid),
            new Claim("unique_name", @"CONTOSO\anna"));

        var resolved = OmpOidcClaimResolver.Resolve(
            principal, BrokenSamAccountNameOptions(), translator);

        Assert.NotNull(resolved);
        Assert.Contains(@"CONTOSO\anna", resolved.UserPrincipalCandidates);
    }

    [Fact]
    public void Resolve_TranslationDisabled_DoesNotTranslateSids()
    {
        var translator = new FakeSidAccountTranslator();
        translator.SidToName[UserSid] = @"CONTOSO\anna";
        var options = BrokenSamAccountNameOptions();
        options.TranslateSidClaimsToAccountNames = false;
        var principal = CreatePrincipal(
            new Claim("sub", "pairwise-subject-1"),
            new Claim("sid", UserSid));

        var resolved = OmpOidcClaimResolver.Resolve(principal, options, translator);

        Assert.NotNull(resolved);
        Assert.DoesNotContain(@"CONTOSO\anna", resolved.UserPrincipalCandidates);
        Assert.Equal(0, translator.SidCalls);
    }

    [Fact]
    public void Resolve_GroupSidClaim_EnrichedWithTranslatedName()
    {
        var translator = new FakeSidAccountTranslator();
        translator.SidToName[GroupSid] = @"CONTOSO\App-Users";
        var principal = CreatePrincipal(
            new Claim("sub", "pairwise-subject-1"),
            new Claim("groups", GroupSid));

        var resolved = OmpOidcClaimResolver.Resolve(principal, new OmpOidcOptions(), translator);

        Assert.NotNull(resolved);
        Assert.Contains(GroupSid, resolved.Groups);
        Assert.Contains(@"CONTOSO\App-Users", resolved.Groups);
    }

    [Fact]
    public void Resolve_GroupNameClaim_EnrichedWithTranslatedSid()
    {
        var translator = new FakeSidAccountTranslator();
        translator.NameToSid[@"CONTOSO\App-Users"] = GroupSid;
        var principal = CreatePrincipal(
            new Claim("sub", "pairwise-subject-1"),
            new Claim("groups", @"CONTOSO\App-Users"));

        var resolved = OmpOidcClaimResolver.Resolve(principal, new OmpOidcOptions(), translator);

        Assert.NotNull(resolved);
        Assert.Contains(@"CONTOSO\App-Users", resolved.Groups);
        Assert.Contains(GroupSid, resolved.Groups);
    }

    [Fact]
    public void Resolve_UniqueNameClaimInUpnForm_BuildsUpnPrincipalCandidates()
    {
        // A real ADFS variant: unique_name arrives in UPN form instead of the
        // DOMAIN\name form. The resolver must surface it as a user-principal
        // candidate and the AD-link lookup must see the upn: alias.
        var principal = CreatePrincipal(
            new Claim("sub", "pairwise-subject-1"),
            new Claim("unique_name", "anna@contoso.example"));

        var resolved = OmpOidcClaimResolver.Resolve(principal, BrokenSamAccountNameOptions());

        Assert.NotNull(resolved);
        Assert.Contains("anna@contoso.example", resolved.UserPrincipalCandidates);
        Assert.Contains("upn:anna@contoso.example", resolved.ProviderUserKeyCandidates);

        var adLookupKeys = OmpAdfsAdAccountLinker.BuildAdProviderLookupKeys(resolved);
        Assert.Contains("upn:anna@contoso.example", adLookupKeys);
    }

    [Fact]
    public void BrokenSamAccountNameMapping_RolePrincipalsStillContainDomainName()
    {
        // The 2026-08-20 incident configuration: SamAccountNameClaimType pointed
        // at a claim the ADFS server never sends, and DomainClaimType at
        // netbiosname. Role resolution must still produce the DOMAIN\name form.
        var translator = new FakeSidAccountTranslator();
        translator.SidToName[UserSid] = @"CONTOSO\anna";
        var principal = CreatePrincipal(
            new Claim("sub", "pairwise-subject-1"),
            new Claim("upn", "anna@contoso.example"),
            new Claim("sid", UserSid),
            new Claim("unique_name", @"CONTOSO\anna"));

        var resolved = OmpOidcClaimResolver.Resolve(
            principal, BrokenSamAccountNameOptions(), translator);
        Assert.NotNull(resolved);

        var rolePrincipals = OmpAuthRepository.BuildOidcRolePrincipals(resolved);

        Assert.Contains(("ADUser", @"CONTOSO\anna"), rolePrincipals);
    }

    [Fact]
    public void BrokenSamAccountNameMapping_AuthenticatedUsersDomainGateStillHolds()
    {
        // RbacService.IsAuthenticatedUsersPrincipalAllowedAsync derives the
        // account domain from User/ADUser principals on the DOMAIN\name form.
        // The enriched claims must keep that gate working end to end.
        var translator = new FakeSidAccountTranslator();
        translator.SidToName[UserSid] = @"CONTOSO\anna";
        var principal = CreatePrincipal(
            new Claim("sub", "pairwise-subject-1"),
            new Claim("sid", UserSid));

        var resolved = OmpOidcClaimResolver.Resolve(
            principal, BrokenSamAccountNameOptions(), translator);
        Assert.NotNull(resolved);

        var ompUser = new OmpAuthenticatedUser
        {
            DisplayName = resolved.DisplayName,
            Provider = resolved.ProviderName,
            ProviderUserKey = resolved.ProviderUserKey,
            RolePrincipals = OmpAuthRepository.BuildOidcRolePrincipals(resolved)
        };

        var domains = RbacService.GetWindowsAccountDomains(ompUser.ToClaimsPrincipal()).ToList();

        Assert.Contains("CONTOSO", domains);
    }

    private static OmpOidcOptions BrokenSamAccountNameOptions()
        => new()
        {
            ProviderName = "ADFS",
            ClaimTypes = new OmpOidcClaimTypeOptions
            {
                SamAccountNameClaimType = "samaccountname", // never sent by the measured ADFS
                DomainClaimType = "netbiosname"             // never sent by the measured ADFS
            }
        };

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "test"));

    private sealed class FakeSidAccountTranslator : IOmpSidAccountTranslator
    {
        public Dictionary<string, string> SidToName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> NameToSid { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int SidCalls { get; private set; }
        public int NameCalls { get; private set; }

        public string? TryTranslateSidToAccountName(string sid)
        {
            SidCalls++;
            return SidToName.GetValueOrDefault(sid);
        }

        public string? TryTranslateAccountNameToSid(string accountName)
        {
            NameCalls++;
            return NameToSid.GetValueOrDefault(accountName);
        }
    }
}
