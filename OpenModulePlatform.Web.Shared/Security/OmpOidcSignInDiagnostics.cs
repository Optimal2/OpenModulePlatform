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

        // Principal values -- account names, and SIDs translated from account names
        // -- are personal data and are never written to the log, not even opt-in
        // (CodeQL cs/cleartext-storage-of-sensitive-information, alerts #10 and
        // #13). What incident diagnostics actually needs is the SHAPE of what the
        // resolver produced: did the SID<->name enrichment yield both forms, and how
        // many of each. That is what is logged. The raw claim values, opted in
        // below, let an operator reconstruct the actual principals when they must.
        logger.LogInformation(
            "OIDC sign-in diagnostics: resolved role principals by type and form: {PrincipalShapes}.",
            BuildPrincipalShapeSummary(resultingRolePrincipals));

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

    /// <summary>
    /// "ADUser|SID (2), ADUser|domain\name (1), OIDCSubject|opaque (1), OmpUser|number (1),
    /// User|upn (1)" — the principal type before the first '|' and the FORM of the value
    /// after it, with a count. The value itself is never part of the output: the form
    /// is a fixed label chosen by a pattern test, so the log shows that the SID/name
    /// enrichment produced both forms without carrying either.
    /// </summary>
    private static string BuildPrincipalShapeSummary(IReadOnlyList<string> principals)
    {
        var summary = principals
            .Select(principal =>
            {
                var separator = principal.IndexOf('|', StringComparison.Ordinal);
                var type = separator > 0 ? principal[..separator] : "(untyped)";
                var value = separator >= 0 ? principal[(separator + 1)..] : principal;
                return type + "|" + ClassifyPrincipalForm(value);
            })
            .GroupBy(shape => shape, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key} ({group.Count()})")
            .ToList();
        return summary.Count == 0 ? "none" : string.Join(", ", summary);
    }

    private static string ClassifyPrincipalForm(string value)
    {
        if (value.Length == 0)
        {
            return "empty";
        }

        if (SidForm.IsMatch(value))
        {
            return "SID";
        }

        if (value.Contains('\\', StringComparison.Ordinal))
        {
            return "domain\\name";
        }

        if (value.Contains('@', StringComparison.Ordinal))
        {
            return "upn";
        }

        if (value.All(char.IsAsciiDigit))
        {
            return "number";
        }

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "issuer-qualified";
        }

        return "opaque";
    }

    private static readonly System.Text.RegularExpressions.Regex SidForm = new(
        @"^S-1-\d+(?:-\d+)+$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

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

        foreach (var missing in FindMissingConfiguredClaimTypes(principal, claimTypes)
            .Where(missing => _reported.TryAdd(missing, 0)))
        {
            logger.LogWarning(
                "OIDC configuration maps claim type {ClaimType}, but that claim was not present in a validated sign-in. Check the OmpAuth:Oidc:ClaimTypes mapping against what the provider actually sends.",
                missing);
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
