using OpenModulePlatform.Web.Shared.Options;
using System.Security.Claims;

namespace OpenModulePlatform.Web.Shared.Security;

public sealed class OmpOidcResolvedClaims
{
    public string ProviderName { get; init; } = OmpAuthDefaults.OidcProviderDisplayName;
    public string Subject { get; init; } = "";
    public string Issuer { get; init; } = "";
    public string ProviderUserKey { get; init; } = "";
    public IReadOnlyList<string> ProviderUserKeyCandidates { get; init; } = [];
    public string UserName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public IReadOnlyList<string> UserPrincipalCandidates { get; init; } = [];
    public IReadOnlyList<string> Groups { get; init; } = [];
}

public static class OmpOidcClaimResolver
{
    private static readonly string[] SubjectFallbackClaimTypes =
    [
        "sub",
        ClaimTypes.NameIdentifier
    ];

    private static readonly string[] ProviderUserKeyFallbackClaimTypes =
    [
        ClaimTypes.PrimarySid,
        "objectsid",
        "sid",
        ClaimTypes.NameIdentifier,
        "sub"
    ];

    private static readonly string[] NameFallbackClaimTypes =
    [
        "preferred_username",
        "upn",
        ClaimTypes.Upn,
        ClaimTypes.WindowsAccountName,
        "samaccountname",
        "sAMAccountName",
        "email",
        "name",
        ClaimTypes.Email,
        ClaimTypes.Name
    ];

    private static readonly string[] UserSidFallbackClaimTypes =
    [
        ClaimTypes.PrimarySid,
        "objectsid",
        "sid"
    ];

    private static readonly string[] UpnFallbackClaimTypes =
    [
        "upn",
        ClaimTypes.Upn,
        "preferred_username"
    ];

    private static readonly string[] SamAccountNameFallbackClaimTypes =
    [
        "samaccountname",
        "sAMAccountName",
        "sam_account_name",
        "accountname"
    ];

    private static readonly string[] DomainFallbackClaimTypes =
    [
        "domain",
        "netbiosname",
        "netbios_domain"
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

        var providerUserKeyClaimValue = FindFirstValue(
            principal,
            options.ClaimTypes.ProviderUserKeyClaimType,
            [options.ClaimTypes.UserIdClaimType, .. ProviderUserKeyFallbackClaimTypes]) ?? subject;
        var issuer = FindFirstValue(principal, "iss") ?? "";
        var providerUserKey = string.IsNullOrWhiteSpace(issuer)
            ? providerUserKeyClaimValue.Trim()
            : string.Concat(issuer.Trim(), "|", providerUserKeyClaimValue.Trim());

        var userName = FindFirstValue(
            principal,
            options.ClaimTypes.NameClaimType,
            NameFallbackClaimTypes) ?? subject;

        var displayName = FindFirstValue(
            principal,
            options.ClaimTypes.DisplayNameClaimType,
            ["name", ClaimTypes.Name]) ?? userName;

        var userPrincipalCandidates = BuildUserPrincipalCandidates(principal, options, userName);

        return new OmpOidcResolvedClaims
        {
            ProviderName = NormalizeProviderName(options.ProviderName),
            Subject = subject.Trim(),
            Issuer = issuer.Trim(),
            ProviderUserKey = providerUserKey,
            ProviderUserKeyCandidates = BuildProviderUserKeyCandidates(
                providerUserKey,
                subject,
                userName,
                userPrincipalCandidates),
            UserName = userName.Trim(),
            DisplayName = displayName.Trim(),
            UserPrincipalCandidates = userPrincipalCandidates,
            Groups = FindAllValues(principal, ResolveGroupClaimTypes(options.ClaimTypes))
        };
    }

    private static string NormalizeProviderName(string? providerName)
        => string.IsNullOrWhiteSpace(providerName)
            ? OmpAuthDefaults.OidcProviderDisplayName
            : providerName.Trim();

    private static IReadOnlyList<string> BuildProviderUserKeyCandidates(
        string providerUserKey,
        string subject,
        string userName,
        IReadOnlyList<string> userPrincipalCandidates)
    {
        var keys = new List<string>
        {
            providerUserKey,
            string.IsNullOrWhiteSpace(subject) ? "" : "sub:" + subject,
            string.IsNullOrWhiteSpace(userName) ? "" : "name:" + userName,
            userName
        };

        foreach (var principal in userPrincipalCandidates)
        {
            if (IsSid(principal))
            {
                keys.Add("sid:" + principal);
            }

            if (principal.Contains('@', StringComparison.Ordinal))
            {
                keys.Add("upn:" + principal);
            }

            keys.Add("name:" + principal);
            keys.Add(principal);
        }

        return NormalizeDistinct(keys);
    }

    private static IReadOnlyList<string> BuildUserPrincipalCandidates(
        ClaimsPrincipal principal,
        OmpOidcOptions options,
        string userName)
    {
        var candidates = new List<string>();

        AddFirstClaimValue(
            candidates,
            principal,
            options.ClaimTypes.UserSidClaimType,
            UserSidFallbackClaimTypes);
        AddFirstClaimValue(
            candidates,
            principal,
            options.ClaimTypes.UpnClaimType,
            UpnFallbackClaimTypes);
        AddFirstClaimValue(
            candidates,
            principal,
            options.ClaimTypes.SamAccountNameClaimType,
            SamAccountNameFallbackClaimTypes);
        AddFirstClaimValue(
            candidates,
            principal,
            ClaimTypes.WindowsAccountName,
            ["windowsaccountname"]);

        var domain = FindFirstValue(
            principal,
            options.ClaimTypes.DomainClaimType,
            DomainFallbackClaimTypes);
        var samAccountName = FindFirstValue(
            principal,
            options.ClaimTypes.SamAccountNameClaimType,
            SamAccountNameFallbackClaimTypes);
        if (!string.IsNullOrWhiteSpace(domain) &&
            !string.IsNullOrWhiteSpace(samAccountName))
        {
            candidates.Add(domain.Trim() + "\\" + samAccountName.Trim());
        }

        candidates.Add(userName);
        return NormalizeDistinct(candidates);
    }

    private static void AddFirstClaimValue(
        List<string> values,
        ClaimsPrincipal principal,
        string? configuredClaimType,
        IReadOnlyList<string> fallbackClaimTypes)
    {
        var value = FindFirstValue(principal, configuredClaimType, fallbackClaimTypes);
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }

    private static IReadOnlyList<string> ResolveGroupClaimTypes(
        OmpOidcClaimTypeOptions claimTypes)
    {
        var result = new List<string>
        {
            claimTypes.GroupsClaimType
        };
        result.AddRange(claimTypes.GroupClaimTypes);
        result.AddRange(claimTypes.GroupSidClaimTypes);
        result.AddRange(claimTypes.GroupNameClaimTypes);
        result.Add(ClaimTypes.GroupSid);
        result.Add(ClaimTypes.Role);
        return NormalizeDistinct(result);
    }

    private static string? FindFirstValue(
        ClaimsPrincipal principal,
        string? configuredClaimType,
        IReadOnlyList<string>? fallbackClaimTypes = null)
        => EnumerateClaimTypes(configuredClaimType, fallbackClaimTypes)
            .Select(claimType => principal.FindFirst(claimType)?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static IReadOnlyList<string> FindAllValues(
        ClaimsPrincipal principal,
        IReadOnlyList<string> claimTypes)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claimType in claimTypes)
        {
            foreach (var value in principal.FindAll(claimType)
                .Select(claim => claim.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                values.Add(value.Trim());
            }
        }

        return values.ToList();
    }

    private static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string?> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsSid(string value)
        => value.StartsWith("S-", StringComparison.OrdinalIgnoreCase);

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

        foreach (var claimType in fallbackClaimTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Where(type => !string.Equals(configuredClaimType, type, StringComparison.Ordinal)))
        {
            yield return claimType;
        }
    }
}
