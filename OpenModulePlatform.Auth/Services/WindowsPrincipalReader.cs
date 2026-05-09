// File: OpenModulePlatform.Auth/Services/WindowsPrincipalReader.cs
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;

namespace OpenModulePlatform.Auth.Services;

public sealed class WindowsPrincipalReader
{
    private readonly ILogger<WindowsPrincipalReader> _logger;

    public WindowsPrincipalReader(ILogger<WindowsPrincipalReader> logger)
    {
        _logger = logger;
    }

    public string? GetUserName(ClaimsPrincipal principal)
        => principal.Identity?.IsAuthenticated == true
            ? principal.Identity.Name
            : null;

    public string? GetUserSid(ClaimsPrincipal principal)
        => OperatingSystem.IsWindows()
            ? GetWindowsUserSid(principal)
            : principal.FindFirstValue(ClaimTypes.PrimarySid);

    public IReadOnlyCollection<string> GetGroupPrincipals(ClaimsPrincipal principal)
    {
        if (!OperatingSystem.IsWindows())
        {
            return GetGroupPrincipalsFromClaims(principal);
        }

        return GetWindowsGroupPrincipals(principal);
    }

    private IReadOnlyCollection<string> GetGroupPrincipalsFromClaims(ClaimsPrincipal principal)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in principal.FindAll(ClaimTypes.GroupSid)
                     .Concat(principal.FindAll(ClaimTypes.Role))
                     .Where(claim => !string.IsNullOrWhiteSpace(claim.Value)))
        {
            result.Add(claim.Value);
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static string? GetWindowsUserSid(ClaimsPrincipal principal)
        => principal.Identity is WindowsIdentity windowsIdentity
            ? windowsIdentity.User?.Value
            : principal.FindFirstValue(ClaimTypes.PrimarySid);

    [SupportedOSPlatform("windows")]
    private IReadOnlyCollection<string> GetWindowsGroupPrincipals(ClaimsPrincipal principal)
        => principal.Identity is WindowsIdentity windowsIdentity
            ? GetGroupPrincipalsFromWindowsIdentity(windowsIdentity)
            : GetGroupPrincipalsFromClaims(principal);

    [SupportedOSPlatform("windows")]
    private IReadOnlyCollection<string> GetGroupPrincipalsFromWindowsIdentity(WindowsIdentity windowsIdentity)
    {
        if (windowsIdentity.Groups is null)
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in windowsIdentity.Groups)
        {
            if (!string.IsNullOrWhiteSpace(sid.Value))
            {
                // Keep the SID even when NTAccount translation succeeds. Role principals may
                // intentionally target either the stable SID or the readable account name.
                result.Add(sid.Value);
            }

            try
            {
                if (sid.Translate(typeof(NTAccount)) is NTAccount account &&
                    !string.IsNullOrWhiteSpace(account.Value))
                {
                    result.Add(account.Value);
                }
            }
            catch (Exception ex) when (
                ex is IdentityNotMappedException or UnauthorizedAccessException)
            {
                // SID translation depends on Windows account lookup infrastructure and can
                // fail for unmapped SIDs or access limitations. Keep the SID principal and
                // log expected readable NTAccount translation failures only. Unexpected
                // translation exceptions are intentionally not swallowed here.
                LogSkippedSidTranslation(ex, sid.Value);
            }
        }

        return result;
    }

    private void LogSkippedSidTranslation(Exception ex, string sidValue)
    {
        _logger.LogDebug(ex, "Skipped SID to NTAccount translation for SID {SidValue}.", sidValue);
    }
}
