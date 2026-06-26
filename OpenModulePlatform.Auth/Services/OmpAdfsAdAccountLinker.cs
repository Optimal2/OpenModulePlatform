using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Auth.Services;

internal enum OmpAdLinkedUserResolutionStatus
{
    NoMatch,
    UniqueActive,
    Disabled,
    AmbiguousActive
}

internal readonly record struct OmpAdLinkedUserCandidate(
    int UserAuthId,
    int UserId,
    string DisplayName,
    int AccountStatus)
{
    public bool IsActive => AccountStatus == 1;
}

internal readonly record struct OmpAdLinkedUserResolution(
    OmpAdLinkedUserResolutionStatus Status,
    OmpAdLinkedUserCandidate? User,
    int MatchedUserCount,
    int ActiveUserCount);

internal static class OmpAdfsAdAccountLinker
{
    public static IReadOnlyList<string> BuildAdProviderLookupKeys(OmpOidcResolvedClaims oidcClaims)
    {
        var keys = new List<string>();

        AddKeys(keys, oidcClaims.ProviderUserKeyCandidates);
        foreach (var principal in oidcClaims.UserPrincipalCandidates)
        {
            AddPrincipalKey(keys, principal);
        }

        return keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Where(key => key.Length <= 1000)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static OmpAdLinkedUserResolution Resolve(IReadOnlyList<OmpAdLinkedUserCandidate> matches)
    {
        if (matches.Count == 0)
        {
            return new OmpAdLinkedUserResolution(
                OmpAdLinkedUserResolutionStatus.NoMatch,
                User: null,
                MatchedUserCount: 0,
                ActiveUserCount: 0);
        }

        var matchedUserCount = matches
            .Select(match => match.UserId)
            .Distinct()
            .Count();
        var activeUsers = matches
            .Where(match => match.IsActive)
            .GroupBy(match => match.UserId)
            .Select(group => group.OrderBy(match => match.UserAuthId).First())
            .OrderBy(match => match.UserAuthId)
            .ToList();

        return activeUsers.Count switch
        {
            0 => new OmpAdLinkedUserResolution(
                OmpAdLinkedUserResolutionStatus.Disabled,
                User: matches.OrderBy(match => match.UserAuthId).First(),
                matchedUserCount,
                ActiveUserCount: 0),
            1 => new OmpAdLinkedUserResolution(
                OmpAdLinkedUserResolutionStatus.UniqueActive,
                User: activeUsers[0],
                matchedUserCount,
                ActiveUserCount: 1),
            _ => new OmpAdLinkedUserResolution(
                OmpAdLinkedUserResolutionStatus.AmbiguousActive,
                User: null,
                matchedUserCount,
                activeUsers.Count)
        };
    }

    private static void AddKeys(List<string> keys, IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            AddCandidateKey(keys, candidate);
        }
    }

    private static void AddCandidateKey(List<string> keys, string? candidate)
    {
        var key = candidate?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (key.StartsWith("sid:", StringComparison.OrdinalIgnoreCase))
        {
            keys.Add(key);
            return;
        }

        if (key.StartsWith("upn:", StringComparison.OrdinalIgnoreCase) &&
            key.Length > "upn:".Length)
        {
            keys.Add(key);
            return;
        }

        if (key.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
        {
            var principal = key["name:".Length..].Trim();
            if (IsDomainAccountName(principal))
            {
                keys.Add("name:" + principal);
            }

            return;
        }

        if (IsDomainAccountName(key))
        {
            keys.Add(key);
        }
    }

    private static void AddPrincipalKey(List<string> keys, string? principal)
    {
        var value = principal?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (IsSid(value))
        {
            keys.Add("sid:" + value);
        }
        else if (IsUpn(value))
        {
            keys.Add("upn:" + value);
        }
        else if (IsDomainAccountName(value))
        {
            keys.Add("name:" + value);
            keys.Add(value);
        }
    }

    private static bool IsDomainAccountName(string value)
    {
        var slashIndex = value.IndexOf('\\', StringComparison.Ordinal);
        return slashIndex > 0 && slashIndex < value.Length - 1;
    }

    private static bool IsSid(string value)
        => value.StartsWith("S-", StringComparison.OrdinalIgnoreCase);

    private static bool IsUpn(string value)
    {
        var atIndex = value.IndexOf('@', StringComparison.Ordinal);
        return atIndex > 0 && atIndex < value.Length - 1;
    }
}
