using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installer.Core;

/// <summary>
/// Applies the operator-typed service account password to the bootstrap config
/// before the shared install chain runs. The IIS application pools run as the
/// profile's <c>iisAppPoolUserName</c>; when that is the same account as the
/// service account and no separate app-pool password is configured, the pools
/// must reuse the service account password the operator typed - creating a pool
/// with a user name and no password leaves every web application stopped.
/// </summary>
public static class OperatorCredentialApplier
{
    public static void Apply(BootstrapConfig config, string serviceAccountPassword)
    {
        var hostAgent = config.HostAgent;
        // Passwords are never trimmed: LogonUser validated the exact typed
        // value, and leading/trailing spaces are legal password characters.
        var password = serviceAccountPassword ?? string.Empty;
        if (password.Length > 0)
        {
            hostAgent.ServiceAccountPassword = password;
        }

        var effectivePassword = string.IsNullOrEmpty(hostAgent.ServiceAccountPassword)
            ? password
            : hostAgent.ServiceAccountPassword;
        if (effectivePassword.Length == 0)
        {
            return;
        }

        if (SameAccount(hostAgent.IisAppPoolUserName, hostAgent.ServiceAccountName)
            && string.IsNullOrWhiteSpace(hostAgent.IisAppPoolPassword)
            && string.IsNullOrWhiteSpace(hostAgent.IisAppPoolPasswordCredentialKey))
        {
            hostAgent.IisAppPoolPassword = effectivePassword;
        }

        foreach (var pair in hostAgent.IisAppPoolOverrides)
        {
            var identity = pair.Value;
            if (identity is null)
            {
                continue;
            }

            if (SameAccount(identity.UserName, hostAgent.ServiceAccountName)
                && string.IsNullOrWhiteSpace(identity.Password)
                && string.IsNullOrWhiteSpace(identity.PasswordCredentialKey))
            {
                identity.Password = effectivePassword;
            }
        }
    }

    /// <summary>
    /// Account comparison tolerant of the forms an operator writes: <c>.\svc</c>,
    /// <c>MACHINE\svc</c>, <c>DOMAIN\svc</c> and <c>svc@domain.example</c> name
    /// the same account when the machine or domain part matches. Case-insensitive.
    /// The machine name is a parameter so the comparison stays testable.
    /// </summary>
    internal static bool SameAccount(string? left, string? right, string? machineName = null)
    {
        var normalizedLeft = NormalizeAccount(left, machineName);
        var normalizedRight = NormalizeAccount(right, machineName);
        return normalizedLeft.Length > 0 && normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAccount(string? account, string? machineName)
    {
        var trimmed = (account ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var localMachineName = string.IsNullOrWhiteSpace(machineName)
            ? Environment.MachineName
            : machineName.Trim();

        if (trimmed.StartsWith(".\\", StringComparison.Ordinal))
        {
            return localMachineName + "\\" + trimmed[2..];
        }

        var atIndex = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (atIndex > 0 && atIndex < trimmed.Length - 1)
        {
            // UPN form: normalize to DOMAIN\user with the NetBIOS domain part,
            // the same normalization the shared install chain applies before
            // granting database access.
            var domain = trimmed[(atIndex + 1)..];
            var dotIndex = domain.IndexOf('.', StringComparison.Ordinal);
            var netBios = (dotIndex > 0 ? domain[..dotIndex] : domain).ToUpperInvariant();
            return netBios.Length == 0 ? trimmed : netBios + "\\" + trimmed[..atIndex];
        }

        if (!trimmed.Contains('\\', StringComparison.Ordinal)
            && !IsBuiltInName(trimmed))
        {
            return localMachineName + "\\" + trimmed;
        }

        return trimmed;
    }

    private static bool IsBuiltInName(string value)
        => value.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || value.Equals("LocalService", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NetworkService", StringComparison.OrdinalIgnoreCase);
}
