// File: OpenModulePlatform.Auth/Services/WindowsPasswordAuthenticator.cs
using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;

namespace OpenModulePlatform.Auth.Services;

public sealed class WindowsPasswordAuthenticator
{
    private const string AuthenticationType = "WindowsPassword";

    private readonly ILogger<WindowsPasswordAuthenticator> _log;

    public WindowsPasswordAuthenticator(ILogger<WindowsPasswordAuthenticator> log)
    {
        _log = log;
    }

    public WindowsPasswordAuthenticationResult Authenticate(string accountName, string password)
    {
        if (!OperatingSystem.IsWindows())
        {
            return WindowsPasswordAuthenticationResult.Failed("Alternate Windows sign-in is only available on Windows.");
        }

        if (!TrySplitAccountName(accountName, out var userName, out var domain))
        {
            return WindowsPasswordAuthenticationResult.Failed("Enter a Windows account name.");
        }

        if (string.IsNullOrEmpty(password))
        {
            return WindowsPasswordAuthenticationResult.Failed("Enter a Windows password.");
        }

        return AuthenticateWindows(accountName.Trim(), userName, domain, password);
    }

    [SupportedOSPlatform("windows")]
    private WindowsPasswordAuthenticationResult AuthenticateWindows(
        string accountName,
        string userName,
        string? domain,
        string password)
    {
        try
        {
            using var context = CreatePrincipalContext(domain);
            if (!context.ValidateCredentials(userName, password, ContextOptions.Negotiate))
            {
                _log.LogWarning(
                    "Alternate Windows sign-in validation failed for account '{AccountName}' using domain '{Domain}'.",
                    accountName,
                    domain);
                return WindowsPasswordAuthenticationResult.Failed("The Windows account name or password is incorrect.");
            }

            var user = FindUserPrincipal(context, userName);
            var principal = CreateClaimsPrincipal(user, accountName, userName, domain);
            return WindowsPasswordAuthenticationResult.Success(principal);
        }
        catch (PrincipalServerDownException ex)
        {
            _log.LogWarning(ex, "Alternate Windows sign-in could not reach the account authority for '{AccountName}'.", accountName);
            return WindowsPasswordAuthenticationResult.Failed("Windows credentials could not be validated.");
        }
        catch (PrincipalOperationException ex)
        {
            _log.LogWarning(ex, "Alternate Windows sign-in failed for account '{AccountName}'.", accountName);
            return WindowsPasswordAuthenticationResult.Failed("Windows credentials could not be validated.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogWarning(ex, "Alternate Windows sign-in was denied while validating account '{AccountName}'.", accountName);
            return WindowsPasswordAuthenticationResult.Failed("Windows credentials could not be validated.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static PrincipalContext CreatePrincipalContext(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return new PrincipalContext(ContextType.Domain);
        }

        return IsLocalDomain(domain)
            ? new PrincipalContext(ContextType.Machine, Environment.MachineName)
            : new PrincipalContext(ContextType.Domain, domain);
    }

    [SupportedOSPlatform("windows")]
    private static UserPrincipal? FindUserPrincipal(PrincipalContext context, string userName)
    {
        if (userName.Contains('@', StringComparison.Ordinal))
        {
            return UserPrincipal.FindByIdentity(context, IdentityType.UserPrincipalName, userName)
                   ?? UserPrincipal.FindByIdentity(context, userName);
        }

        return UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, userName)
               ?? UserPrincipal.FindByIdentity(context, userName);
    }

    [SupportedOSPlatform("windows")]
    private ClaimsPrincipal CreateClaimsPrincipal(
        UserPrincipal? user,
        string accountName,
        string userName,
        string? domain)
    {
        var canonicalName = GetCanonicalAccountName(user, accountName, userName, domain);
        var identity = new ClaimsIdentity(AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, canonicalName));

        if (!string.IsNullOrWhiteSpace(user?.Sid?.Value))
        {
            identity.AddClaim(new Claim(ClaimTypes.PrimarySid, user.Sid.Value));
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Sid.Value));
        }

        foreach (var group in GetAuthorizationGroups(user, canonicalName))
        {
            AddGroupClaims(identity, group);
        }

        return new ClaimsPrincipal(identity);
    }

    [SupportedOSPlatform("windows")]
    private IEnumerable<Principal> GetAuthorizationGroups(UserPrincipal? user, string canonicalName)
    {
        if (user is null)
        {
            return [];
        }

        try
        {
            return user.GetAuthorizationGroups().ToArray();
        }
        catch (PrincipalOperationException ex)
        {
            _log.LogWarning(ex, "Could not enumerate authorization groups for alternate Windows sign-in account '{AccountName}'.", canonicalName);
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddGroupClaims(ClaimsIdentity identity, Principal group)
    {
        if (!string.IsNullOrWhiteSpace(group.Sid?.Value))
        {
            identity.AddClaim(new Claim(ClaimTypes.GroupSid, group.Sid.Value));

            var ntAccount = TryTranslateSidToNtAccount(group.Sid);
            if (!string.IsNullOrWhiteSpace(ntAccount))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, ntAccount));
            }
        }
        else if (!string.IsNullOrWhiteSpace(group.Name))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, group.Name));
        }
    }

    private static bool TrySplitAccountName(
        string? accountName,
        out string userName,
        out string? domain)
    {
        userName = string.Empty;
        domain = null;

        accountName = accountName?.Trim();
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return false;
        }

        var slashIndex = accountName.IndexOf('\\');
        if (slashIndex >= 0)
        {
            domain = NormalizeLocalDomain(accountName[..slashIndex].Trim());
            userName = accountName[(slashIndex + 1)..].Trim();
            return !string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(userName);
        }

        userName = accountName;
        domain = accountName.Contains('@', StringComparison.Ordinal) ? null : Environment.MachineName;
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static string GetCanonicalAccountName(
        UserPrincipal? user,
        string accountName,
        string userName,
        string? domain)
    {
        if (accountName.Contains('\\', StringComparison.Ordinal))
        {
            return accountName;
        }

        if (!string.IsNullOrWhiteSpace(domain))
        {
            return domain + "\\" + (user?.SamAccountName ?? userName);
        }

        return user?.UserPrincipalName ?? accountName;
    }

    private static string? NormalizeLocalDomain(string? domain)
        => string.Equals(domain, ".", StringComparison.Ordinal)
            ? Environment.MachineName
            : domain;

    private static bool IsLocalDomain(string domain)
        => string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static string? TryTranslateSidToNtAccount(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)) is NTAccount account
                ? account.Value
                : null;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (SystemException)
        {
            return null;
        }
    }
}

public sealed record WindowsPasswordAuthenticationResult(
    ClaimsPrincipal? Principal,
    string? ErrorMessage)
{
    public bool Succeeded => Principal is not null;

    public static WindowsPasswordAuthenticationResult Success(ClaimsPrincipal principal)
        => new(principal, null);

    public static WindowsPasswordAuthenticationResult Failed(string errorMessage)
        => new(null, errorMessage);
}
