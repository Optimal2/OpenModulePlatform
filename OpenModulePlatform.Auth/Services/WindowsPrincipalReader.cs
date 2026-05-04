// File: OpenModulePlatform.Auth/Services/WindowsPrincipalReader.cs
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;

namespace OpenModulePlatform.Auth.Services;

public sealed class WindowsPrincipalReader
{
    private readonly ILogger<WindowsPrincipalReader> _log;

    public WindowsPrincipalReader(ILogger<WindowsPrincipalReader> log)
    {
        _log = log;
    }

    public string? GetUserName(ClaimsPrincipal principal)
        => principal.Identity?.IsAuthenticated == true
            ? principal.Identity.Name
            : null;

    public string? GetUserSid(ClaimsPrincipal principal)
        => OperatingSystem.IsWindows() && principal.Identity is WindowsIdentity windowsIdentity
            ? windowsIdentity.User?.Value
            : principal.FindFirstValue(ClaimTypes.PrimarySid);

    public IReadOnlyList<string> GetGroupPrincipals(ClaimsPrincipal principal)
    {
        if (!OperatingSystem.IsWindows() ||
            principal.Identity is not WindowsIdentity windowsIdentity)
        {
            return [];
        }

        return GetGroupPrincipals(windowsIdentity);
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<string> GetGroupPrincipals(WindowsIdentity windowsIdentity)
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
            catch (IdentityNotMappedException ex)
            {
                _log.LogDebug(ex, "Skipped SID to NTAccount translation for SID {SidValue}.", sid.Value);
            }
            catch (SystemException ex)
            {
                _log.LogDebug(ex, "Skipped SID to NTAccount translation for SID {SidValue}.", sid.Value);
            }
        }

        return result.ToList();
    }
}
