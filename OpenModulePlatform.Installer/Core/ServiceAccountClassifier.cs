namespace OpenModulePlatform.Installer.Core;

/// <summary>What kind of Windows account the HostAgent service is configured to run as.</summary>
public enum ServiceAccountKind
{
    /// <summary>No account configured: the service runs as LocalSystem.</summary>
    NotConfigured,

    /// <summary>A built-in identity (LocalSystem, LocalService, NetworkService, virtual accounts). Never needs a password.</summary>
    BuiltIn,

    /// <summary>An account on this computer (<c>.\name</c>, <c>MACHINE\name</c> or a bare name). Needs a password.</summary>
    LocalMachine,

    /// <summary>A domain account (<c>DOMAIN\name</c> or <c>name@domain</c>). Needs a password.</summary>
    Domain
}

/// <summary>The classified service account, with the parts a credential check needs.</summary>
public sealed record ServiceAccountInfo(
    ServiceAccountKind Kind,
    string DisplayName,
    string LogonUserName,
    string LogonDomain)
{
    /// <summary>Built-in and unconfigured accounts are created without a password.</summary>
    public bool RequiresPassword => Kind is ServiceAccountKind.LocalMachine or ServiceAccountKind.Domain;
}

/// <summary>
/// Classifies the configured HostAgent service account. Pure: the local machine
/// name is supplied by the caller so the classification can be exercised without
/// touching the environment.
/// </summary>
public static class ServiceAccountClassifier
{
    private static readonly string[] BuiltInNames =
    [
        "localsystem",
        "localservice",
        "networkservice",
        @"nt authority\system",
        @"nt authority\localservice",
        @"nt authority\local service",
        @"nt authority\networkservice",
        @"nt authority\network service"
    ];

    public static ServiceAccountInfo Classify(string? configuredAccountName, string localMachineName)
    {
        var account = (configuredAccountName ?? string.Empty).Trim();
        if (account.Length == 0)
        {
            return new ServiceAccountInfo(ServiceAccountKind.NotConfigured, "LocalSystem", string.Empty, string.Empty);
        }

        var machineName = string.IsNullOrWhiteSpace(localMachineName) ? "." : localMachineName.Trim();
        if (BuiltInNames.Contains(account.ToLowerInvariant())
            || account.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase)
            || account.StartsWith(@"IIS APPPOOL\", StringComparison.OrdinalIgnoreCase))
        {
            return new ServiceAccountInfo(ServiceAccountKind.BuiltIn, account, string.Empty, string.Empty);
        }

        var atIndex = account.IndexOf('@', StringComparison.Ordinal);
        if (atIndex > 0 && atIndex < account.Length - 1)
        {
            // UPN form: LogonUser takes the whole value as the user name.
            return new ServiceAccountInfo(ServiceAccountKind.Domain, account, account, string.Empty);
        }

        var slashIndex = account.IndexOf('\\', StringComparison.Ordinal);
        if (slashIndex > 0 && slashIndex < account.Length - 1)
        {
            var domain = account[..slashIndex];
            var userName = account[(slashIndex + 1)..];
            if (domain.Equals(".", StringComparison.Ordinal)
                || domain.Equals(machineName, StringComparison.OrdinalIgnoreCase))
            {
                return new ServiceAccountInfo(
                    ServiceAccountKind.LocalMachine,
                    account,
                    userName,
                    ".");
            }

            return new ServiceAccountInfo(ServiceAccountKind.Domain, account, userName, domain);
        }

        // A bare name is a local account on this machine.
        return new ServiceAccountInfo(ServiceAccountKind.LocalMachine, account, account, ".");
    }
}
