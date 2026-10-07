using OpenModulePlatform.Installation;

namespace OpenModulePlatform.Installer.Core;

/// <summary>
/// Evaluates every prerequisite of the matched profile against an
/// <see cref="IPrerequisiteEnvironment"/>. Read-only by construction: the
/// environment interface has no mutating members, so both the UI's Re-check
/// and <c>--dry-run</c> share this exact code path.
/// </summary>
public static class PrerequisiteEvaluator
{
    /// <summary>Free disk space required on the install drive.</summary>
    public const long RequiredFreeDiskBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>IIS feature names: key = logical feature, value = (server, client) feature name.</summary>
    public static readonly IReadOnlyDictionary<string, (string Server, string Client)> IisFeatureNames =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["web-server"] = ("Web-Server", "IIS-WebServer"),
            ["windows-authentication"] = ("Web-Windows-Auth", "IIS-WindowsAuthentication"),
            ["web-sockets"] = ("Web-WebSockets", "IIS-WebSockets")
        };

    public static async Task<PrerequisiteEvaluation> EvaluateAsync(
        BootstrapConfig config,
        string payloadRoot,
        IPrerequisiteEnvironment environment,
        bool serviceAccountPasswordValidated,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<PrerequisiteCheckResult>();
        var missingFeatures = new List<string>();
        var isServer = environment.IsServerOs;

        string FeatureName(string key) => isServer ? IisFeatureNames[key].Server : IisFeatureNames[key].Client;
        bool FeatureInstalled(string key)
            => environment.IsFeatureInstalled(FeatureName(key)) == true;

        checks.Add(new PrerequisiteCheckResult(
            PrerequisiteCheckId.WindowsEdition,
            IsSatisfied: true,
            "Windows edition",
            environment.WindowsEditionName));

        var iisInstalled = FeatureInstalled("web-server");
        if (!iisInstalled)
        {
            missingFeatures.Add(FeatureName("web-server"));
        }

        checks.Add(new PrerequisiteCheckResult(
            PrerequisiteCheckId.IisWebServer,
            iisInstalled,
            "IIS web server",
            iisInstalled ? "Installed." : "The IIS web server role is not installed.")
        { CanAutoFix = true });

        if (ProfileTargetResolver.ProfileUsesWindowsAuthentication(config))
        {
            var installed = FeatureInstalled("windows-authentication");
            if (!installed)
            {
                missingFeatures.Add(FeatureName("windows-authentication"));
            }

            checks.Add(new PrerequisiteCheckResult(
                PrerequisiteCheckId.IisWindowsAuthentication,
                installed,
                "IIS Windows authentication",
                installed
                    ? "Installed."
                    : "The profile deploys the authentication application, which needs the Windows Authentication role service.")
            { CanAutoFix = true });
        }

        if (ProfileTargetResolver.ProfileUsesBlazor(config))
        {
            var installed = FeatureInstalled("web-sockets");
            if (!installed)
            {
                missingFeatures.Add(FeatureName("web-sockets"));
            }

            checks.Add(new PrerequisiteCheckResult(
                PrerequisiteCheckId.IisWebSockets,
                installed,
                "IIS WebSocket Protocol",
                installed
                    ? "Installed."
                    : "The profile deploys a Blazor Server application, which needs the WebSocket Protocol role service.")
            { CanAutoFix = true });
        }

        var requiredMajor = RuntimeRequirementResolver.ResolveRequiredMajor(
            environment.FindRuntimeConfigTexts(payloadRoot));
        var installedMajor = environment.GetHighestAspNetCoreRuntimeMajor();
        var runtimeOk = installedMajor is >= 1 && installedMajor.Value >= requiredMajor;
        checks.Add(new PrerequisiteCheckResult(
            PrerequisiteCheckId.AspNetCoreRuntime,
            runtimeOk,
            $"ASP.NET Core runtime {requiredMajor}",
            runtimeOk
                ? $"Version {installedMajor} is installed."
                : installedMajor is null
                    ? "No ASP.NET Core shared runtime was found."
                    : $"Only version {installedMajor} is installed.")
        { CanAutoFix = true });

        var moduleRegistered = environment.IsAspNetCoreIisModuleRegistered();
        var moduleDllExists = environment.AspNetCoreIisModuleDllExists();
        var moduleOk = moduleRegistered && moduleDllExists;
        checks.Add(new PrerequisiteCheckResult(
            PrerequisiteCheckId.AspNetCoreIisModule,
            moduleOk,
            "ASP.NET Core IIS module",
            moduleOk
                ? "AspNetCoreModuleV2 is registered with IIS."
                : "AspNetCoreModuleV2 is not correctly registered with IIS.")
        { CanAutoFix = true });

        var bundleRequired = !runtimeOk || !moduleOk;
        var bundleRepair = runtimeOk && !moduleOk;
        var bundlePath = RuntimeRequirementResolver.PickHostingBundleInstaller(
            environment.GetAvailableHostingBundleInstallers(),
            requiredMajor);
        if (bundleRequired)
        {
            checks.Add(new PrerequisiteCheckResult(
                PrerequisiteCheckId.HostingBundleInstaller,
                bundlePath is not null,
                "ASP.NET Core Hosting Bundle installer",
                bundlePath is not null
                    ? $"Will run {Path.GetFileName(bundlePath)}."
                    : $"Place the .NET {requiredMajor} hosting bundle installer (dotnet-hosting-{requiredMajor}.*-win.exe) in the package's prereqs folder."));
        }

        var sql = config.Sql;
        var (sqlOk, sqlDetail) = sql.Enabled
            ? await environment.ProbeSqlDatabaseAsync(
                sql.Server,
                sql.Database,
                sql.TrustServerCertificate,
                cancellationToken)
            : (true, "SQL is disabled in this profile.");
        checks.Add(new PrerequisiteCheckResult(
            PrerequisiteCheckId.SqlDatabase,
            sqlOk,
            "SQL Server database",
            sqlDetail));

        var installRoot = FirstNonWhiteSpace(
            config.HostAgent.InstallPath,
            config.HostAgent.ServicesRoot,
            config.ArtifactStoreRoot);
        var freeBytes = string.IsNullOrWhiteSpace(installRoot)
            ? RequiredFreeDiskBytes
            : environment.GetFreeDiskBytes(installRoot);
        checks.Add(new PrerequisiteCheckResult(
            PrerequisiteCheckId.FreeDiskSpace,
            freeBytes >= RequiredFreeDiskBytes,
            "Free disk space",
            $"{freeBytes / (1024 * 1024):n0} MB free on the install drive (at least {RequiredFreeDiskBytes / (1024 * 1024):n0} MB required)."));

        var serviceAccount = ServiceAccountClassifier.Classify(
            config.HostAgent.ServiceAccountName,
            environment.MachineName);
        var accountOk = !serviceAccount.RequiresPassword || serviceAccountPasswordValidated;
        checks.Add(new PrerequisiteCheckResult(
            PrerequisiteCheckId.ServiceAccount,
            accountOk,
            "Service account",
            serviceAccount.Kind switch
            {
                ServiceAccountKind.NotConfigured => "The HostAgent service runs as LocalSystem; no password is needed.",
                ServiceAccountKind.BuiltIn => $"The HostAgent service runs as {serviceAccount.DisplayName}; no password is needed.",
                _ => accountOk
                    ? $"Password for {serviceAccount.DisplayName} verified."
                    : $"Enter and verify the password for {serviceAccount.DisplayName}."
            }));

        return new PrerequisiteEvaluation(
            checks,
            isServer,
            missingFeatures,
            bundleRequired,
            bundleRepair,
            bundlePath,
            requiredMajor,
            serviceAccount);
    }

    private static string FirstNonWhiteSpace(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
