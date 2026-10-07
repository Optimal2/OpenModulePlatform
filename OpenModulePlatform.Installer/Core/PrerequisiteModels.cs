namespace OpenModulePlatform.Installer.Core;

/// <summary>Stable identifiers for the prerequisite checks (used by tests and the plan).</summary>
public enum PrerequisiteCheckId
{
    WindowsEdition,
    IisWebServer,
    IisWindowsAuthentication,
    IisWebSockets,
    AspNetCoreRuntime,
    AspNetCoreIisModule,
    HostingBundleInstaller,
    SqlDatabase,
    FreeDiskSpace,
    ServiceAccount
}

/// <summary>One prerequisite line on the panel: green when <see cref="IsSatisfied"/>.</summary>
public sealed record PrerequisiteCheckResult(
    PrerequisiteCheckId Id,
    bool IsSatisfied,
    string Title,
    string Detail)
{
    /// <summary>A check the installer can fix itself (IIS feature, hosting bundle).</summary>
    public bool CanAutoFix { get; init; }
}

/// <summary>The full prerequisite evaluation for the matched profile.</summary>
public sealed record PrerequisiteEvaluation(
    IReadOnlyList<PrerequisiteCheckResult> Checks,
    bool IsServerOs,
    IReadOnlyList<string> MissingIisFeatures,
    bool HostingBundleRequired,
    bool HostingBundleRepairRequired,
    string? HostingBundleInstallerPath,
    int RequiredRuntimeMajor,
    ServiceAccountInfo ServiceAccount)
{
    /// <summary>Every red line the installer cannot fix itself blocks Install; an unverified service-account password blocks it too.</summary>
    public bool CanInstall => Checks
        .Where(check => !check.IsSatisfied)
        .All(check => check.CanAutoFix);
}
