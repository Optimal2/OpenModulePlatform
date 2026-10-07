namespace OpenModulePlatform.Installer.Install;

/// <summary>
/// Every mutating or external action of the install flow, behind one seam so
/// that <c>--dry-run</c> never touches them and tests can record/throw.
/// </summary>
public interface IInstallActions
{
    /// <summary>
    /// Installs the named Windows features (Install-WindowsFeature on Server,
    /// dism.exe /enable-feature on client). Returns true when Windows reports a
    /// restart is required (exit code 3010/1641, or RestartNeeded on Server) -
    /// success, not a failure.
    /// </summary>
    bool InstallIisFeatures(IReadOnlyList<string> featureNames, bool isServerOs);

    /// <summary>Runs the hosting-bundle installer. Returns its exit code (0 = success, 3010/1641 = success, restart required).</summary>
    int RunHostingBundle(string installerPath, bool repair);

    /// <summary>Restarts IIS (iisreset) after feature or hosting-bundle changes.</summary>
    void RestartIis();

    /// <summary>Grants the account the "Log on as a service" right. Returns true when the right is in place afterwards.</summary>
    bool EnsureServiceLogonRight(string accountName, out string detail);

    /// <summary>Validates an account password with LogonUser. Returns true when the credentials are accepted.</summary>
    bool ValidateAccountPassword(string userName, string domain, string password, out string error);

    /// <summary>
    /// Waits until the HostAgent has reported an active runtime state for this
    /// host and service at or after <paramref name="notBefore"/>. Read-only SQL
    /// polling. Returns false on timeout.
    /// </summary>
    Task<bool> WaitForHostAgentActiveAsync(
        string server,
        string database,
        bool trustServerCertificate,
        string hostKey,
        string serviceName,
        DateTimeOffset notBefore,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>Probes the portal URL until it answers with a non-server-error HTTP status. Returns false on timeout.</summary>
    Task<(bool Ok, string Detail)> ProbePortalAsync(string portalUrl, TimeSpan timeout, CancellationToken cancellationToken);
}
