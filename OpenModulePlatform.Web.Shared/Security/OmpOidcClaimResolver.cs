using OpenModulePlatform.Web.Shared.Options;
using System.Security.Claims;

namespace OpenModulePlatform.Web.Shared.Security;

public sealed class OmpOidcResolvedClaims
{
    public string Subject { get; init; } = "";
    public string Issuer { get; init; } = "";
    public string ProviderUserKey { get; init; } = "";
    public string UserName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public IReadOnlyList<string> Groups { get; init; } = [];
}

public static class OmpOidcClaimResolver
{
    private static readonly string[] SubjectFallbackClaimTypes =
    [
        "sub",
        ClaimTypes.NameIdentifier
    ];

    private static readonly string[] NameFallbackClaimTypes =
    [
        "preferred_username",
        "upn",
        "email",
        "name",
        ClaimTypes.Upn,
        ClaimTypes.Email,
        ClaimTypes.Name
    ];

    public static OmpOidcResolvedClaims? Resolve(
        ClaimsPrincipal principal,
        OmpOidcOptions options)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);

        var subject = FindFirstValue(
            principal,
            options.ClaimTypes.UserIdClaimType,
            SubjectFallbackClaimTypes);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var issuer = FindFirstValue(principal, "iss") ?? "";
        var providerUserKey = string.IsNullOrWhiteSpace(issuer)
            ? subject.Trim()
            : string.Concat(issuer.Trim(), "|", subject.Trim());

        var userName = FindFirstValue(
            principal,
            options.ClaimTypes.NameClaimType,
            NameFallbackClaimTypes) ?? subject;

        var displayName = FindFirstValue(
            principal,
            options.ClaimTypes.DisplayNameClaimType,
            ["name", ClaimTypes.Name]) ?? userName;

        return new OmpOidcResolvedClaims
        {
            Subject = subject.Trim(),
            Issuer = issuer.Trim(),
            ProviderUserKey = providerUserKey,
            UserName = userName.Trim(),
            DisplayName = displayName.Trim(),
            Groups = FindAllValues(principal, options.ClaimTypes.GroupsClaimType)
        };
    }

    private static string? FindFirstValue(
        ClaimsPrincipal principal,
        string? configuredClaimType,
        IReadOnlyList<string>? fallbackClaimTypes = null)
    {
        foreach (var claimType in EnumerateClaimTypes(configuredClaimType, fallbackClaimTypes))
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> FindAllValues(
        ClaimsPrincipal principal,
        string? configuredClaimType)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claimType in EnumerateClaimTypes(configuredClaimType, [ClaimTypes.GroupSid, ClaimTypes.Role]))
        {
            foreach (var claim in principal.FindAll(claimType))
            {
                if (!string.IsNullOrWhiteSpace(claim.Value))
                {
                    values.Add(claim.Value.Trim());
                }
            }
        }

        return values.ToList();
    }

    private static IEnumerable<string> EnumerateClaimTypes(
        string? configuredClaimType,
        IReadOnlyList<string>? fallbackClaimTypes)
    {
        if (!string.IsNullOrWhiteSpace(configuredClaimType))
        {
            yield return configuredClaimType.Trim();
        }

        if (fallbackClaimTypes is null)
        {
            yield break;
        }

        foreach (var claimType in fallbackClaimTypes.Where(type => !string.IsNullOrWhiteSpace(type)))
        {
            if (!string.Equals(configuredClaimType, claimType, StringComparison.Ordinal))
            {
                yield return claimType;
            }
        }
    }
}
