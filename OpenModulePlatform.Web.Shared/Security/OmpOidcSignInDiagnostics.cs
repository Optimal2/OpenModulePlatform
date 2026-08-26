using Microsoft.Extensions.Logging;
using OpenModulePlatform.Web.Shared.Options;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace OpenModulePlatform.Web.Shared.Security;

/// <summary>
/// Opt-in diagnostics for validated OIDC sign-ins.
/// </summary>
/// <remarks>
/// Why this exists: the OIDC sign-in replaces the provider principal with OMP's
/// own (<c>OnTokenValidated</c> assigns <c>context.Principal</c>) and
/// <c>SaveTokens</c> is false, so after a sign-in nothing records what the
/// provider actually sent. During the 2026-08-19..21 ADFS incident, "the IdP
/// sent nothing", "the wrong claim type was configured" and "the right claim
/// arrived in the wrong form" were indistinguishable in the logs. With
/// diagnostics enabled, each sign-in logs the incoming claim types, the value
/// count per type, and the role principals they resolved to. Raw claim values
/// are only logged when <c>IncludeClaimValues</c> is set, because values can
/// contain personal data.
/// </remarks>
public static class OmpOidcSignInDiagnostics
{
    public static void LogSignIn(
        ILogger logger,
        ClaimsPrincipal incomingPrincipal,
        OmpOidcResolvedClaims resolved,
        IReadOnlyList<string> resultingRolePrincipals,
        OmpOidcDiagnosticsOptions diagnostics)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(incomingPrincipal);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(resultingRolePrincipals);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (!diagnostics.Enabled)
        {
            return;
        }

        logger.LogInformation(
            "OIDC sign-in diagnostics: incoming claim types with value counts: {ClaimTypes}.",
            BuildClaimTypeSummary(incomingPrincipal));

        logger.LogInformation(
            "OIDC sign-in diagnostics: resolved role principals: {Principals}.",
            string.Join(", ", resultingRolePrincipals));

        if (!diagnostics.IncludeClaimValues)
        {
            return;
        }

        LogClaimValues(logger, incomingPrincipal);
    }

    /// <summary>
    /// Logs the incoming claim types with value counts for a sign-in that FAILED
    /// in <c>OnTokenValidated</c> — no configured provider user key, or a matched
    /// but disabled user. Those failures are exactly the cases incident
    /// diagnostics was built to tell apart (campaign
    /// ad-principalformen-hela-vagen-adfs-till-rbac, follow-up phase 2, finding 4),
    /// so they get the same claim-type summary as a successful sign-in. Raw claim
    /// values are only logged when <c>IncludeClaimValues</c> is set.
    /// </summary>
    public static void LogFailedSignIn(
        ILogger logger,
        ClaimsPrincipal? incomingPrincipal,
        OmpOidcDiagnosticsOptions diagnostics)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (!diagnostics.Enabled || incomingPrincipal is null)
        {
            return;
        }

        logger.LogInformation(
            "OIDC sign-in diagnostics (failed sign-in): incoming claim types with value counts: {ClaimTypes}.",
            BuildClaimTypeSummary(incomingPrincipal));

        if (!diagnostics.IncludeClaimValues)
        {
            return;
        }

        LogClaimValues(logger, incomingPrincipal);
    }

    private static string BuildClaimTypeSummary(ClaimsPrincipal incomingPrincipal)
    {
        var claimTypeSummary = incomingPrincipal.Claims
            .GroupBy(claim => claim.Type, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key} ({group.Count()})");
        return string.Join(", ", claimTypeSummary);
    }

    private static void LogClaimValues(ILogger logger, ClaimsPrincipal incomingPrincipal)
    {
        foreach (var group in incomingPrincipal.Claims
                     .GroupBy(claim => claim.Type, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            logger.LogInformation(
                "OIDC sign-in diagnostics: claim {ClaimType} value(s): {ClaimValues}.",
                group.Key,
                string.Join(", ", group.Select(claim => claim.Value)));
        }
    }
}

/// <summary>
/// Warns once per process when a configured claim-type mapping was absent from
/// a validated sign-in.
/// </summary>
/// <remarks>
/// A mapping that points at a claim the provider never sends fails silently
/// otherwise (the 2026-08-20 samaccountname/netbiosname misconfiguration). Only
/// the AD-mapping knobs are checked: their defaults are empty, so a provider
/// that legitimately omits them produces no noise, while an operator-entered
/// mapping that never arrives becomes visible. Reporting is once per claim type
/// per process, so the log is not spammed per request.
/// </remarks>
public sealed class OmpOidcConfiguredClaimReporter
{
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    public void ReportMissingConfiguredClaimTypes(
        ILogger logger,
        ClaimsPrincipal principal,
        OmpOidcClaimTypeOptions claimTypes)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(claimTypes);

        foreach (var missing in FindMissingConfiguredClaimTypes(principal, claimTypes))
        {
            if (_reported.TryAdd(missing, 0))
            {
                logger.LogWarning(
                    "OIDC configuration maps claim type {ClaimType}, but that claim was not present in a validated sign-in. Check the OmpAuth:Oidc:ClaimTypes mapping against what the provider actually sends.",
                    missing);
            }
        }
    }

    internal static IReadOnlyList<string> FindMissingConfiguredClaimTypes(
        ClaimsPrincipal principal,
        OmpOidcClaimTypeOptions claimTypes)
    {
        var configured = new List<string>();

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                configured.Add(value.Trim());
            }
        }

        Add(claimTypes.SamAccountNameClaimType);
        Add(claimTypes.DomainClaimType);
        Add(claimTypes.UserSidClaimType);
        foreach (var claimType in claimTypes.GroupClaimTypes)
        {
            Add(claimType);
        }

        foreach (var claimType in claimTypes.GroupSidClaimTypes)
        {
            Add(claimType);
        }

        foreach (var claimType in claimTypes.GroupNameClaimTypes)
        {
            Add(claimType);
        }

        return configured
            .Distinct(StringComparer.Ordinal)
            .Where(claimType => !principal.Claims.Any(
                claim => string.Equals(claim.Type, claimType, StringComparison.Ordinal)))
            .ToList();
    }
}
