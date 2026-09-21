using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Restricts <c>hostagent.credentials.json</c> to the accounts that must read it.
/// </summary>
/// <remarks>
/// The store is protected with DPAPI in <c>LocalMachine</c> scope and a well-known
/// entropy string, so every process on the host that can read the file can also
/// decrypt it. The file ACL is therefore the real boundary. The manual script
/// (<c>scripts/deployment/set-hostagent-credential-store.ps1</c>) already sets a
/// protected ACL of Administrators, SYSTEM and the service account; the C# writers
/// (bootstrapper install, HostAgent upsert, self-upgrade copy) left the file with
/// whatever the install directory happened to inherit. This helper gives all of
/// them the same ACL: inheritance is cut and only Administrators, SYSTEM, the
/// account writing the file and the configured service account(s) keep access.
/// </remarks>
public static class HostAgentCredentialStoreFileAcl
{
    /// <summary>
    /// Replaces the file's DACL with a protected (non-inherited) DACL granting full
    /// control to Administrators, SYSTEM, the current process identity and
    /// <paramref name="serviceAccountNames"/>. Blank names are ignored. No-op off
    /// Windows, where the DPAPI store cannot be written anyway.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A configured service account cannot be resolved to a SID. The store would
    /// otherwise be written with an ACL the service cannot read.
    /// </exception>
    public static void Apply(string path, params string[] serviceAccountNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ApplyWindows(path, serviceAccountNames);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(string path, IEnumerable<string> serviceAccountNames)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl(AccessControlSections.Access);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // Start from an empty explicit list so the result is exactly the allowed set,
        // regardless of what an earlier writer or a copy left on the file.
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleAll(rule);
        }

        foreach (var sid in ResolveGrantees(serviceAccountNames))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        file.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<SecurityIdentifier> ResolveGrantees(IEnumerable<string> serviceAccountNames)
    {
        var grantees = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.LocalSystemSid, null)
        };

        // The writer keeps access to what it wrote: the HostAgent service upserting its
        // own store must still be able to read it back.
        using (var current = WindowsIdentity.GetCurrent())
        {
            if (current.User is not null)
            {
                grantees.Add(current.User);
            }
        }

        foreach (var accountName in serviceAccountNames)
        {
            var sid = TryResolveSid(accountName);
            if (sid is not null)
            {
                grantees.Add(sid);
            }
        }

        return grantees;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier? TryResolveSid(string? accountName)
    {
        var trimmed = accountName?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }

        // sc.exe spellings of the built-in service accounts.
        if (trimmed.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase))
        {
            return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        }

        if (trimmed.Equals("LocalService", StringComparison.OrdinalIgnoreCase))
        {
            return new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        }

        if (trimmed.Equals("NetworkService", StringComparison.OrdinalIgnoreCase))
        {
            return new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        }

        if (trimmed.StartsWith(".\\", StringComparison.Ordinal))
        {
            trimmed = Environment.MachineName + "\\" + trimmed[2..];
        }

        try
        {
            return (SecurityIdentifier)new NTAccount(trimmed).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException ex)
        {
            throw new InvalidOperationException(
                $"HostAgent credential store ACL: service account '{trimmed}' could not be resolved to a Windows account.",
                ex);
        }
    }
}
