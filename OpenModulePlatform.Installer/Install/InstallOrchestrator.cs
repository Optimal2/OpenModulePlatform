using OpenModulePlatform.Installation;
using OpenModulePlatform.Installer.Core;

namespace OpenModulePlatform.Installer.Install;

/// <summary>Everything one install run needs.</summary>
public sealed record InstallRequest(
    BootstrapConfig Config,
    string ConfigPath,
    string PayloadRoot,
    PrerequisiteEvaluation Evaluation,
    string OperatorPassword);

/// <summary>
/// Runs the first installation: fix what the prerequisite evaluation found
/// (IIS features, hosting bundle, service logon right), then the shared
/// install chain in exactly the Bootstrapper's bootstrap order - except that
/// the database is NEVER created (it must already exist; the prerequisite
/// check proved it). With <c>dryRun</c> every check still runs and the plan
/// is reported, but no mutating action, SQL statement, service or IIS call is
/// made.
/// </summary>
public static class InstallOrchestrator
{
    public static async Task RunAsync(
        InstallRequest request,
        IInstallActions actions,
        IInstallProgress progress,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var evaluation = request.Evaluation;
        var account = evaluation.ServiceAccount;

        if (dryRun)
        {
            ReportPlan(request, progress);
            return;
        }

        // 1. The service account password must validate before anything changes.
        if (account.RequiresPassword)
        {
            progress.Info($"> Verify service account {account.DisplayName}");
            if (!actions.ValidateAccountPassword(account.LogonUserName, account.LogonDomain, request.OperatorPassword, out var error))
            {
                throw new InvalidOperationException($"The password for {account.DisplayName} was not accepted: {error}");
            }
        }

        // 2. "Log on as a service" is not granted by sc.exe; grant it up front so
        //    the service the chain creates can actually start.
        if (account.RequiresPassword)
        {
            progress.Info($"> Grant 'Log on as a service' to {account.DisplayName}");
            if (actions.EnsureServiceLogonRight(account.DisplayName, out var detail))
            {
                progress.Info($"  {detail}");
            }
            else
            {
                throw new InvalidOperationException($"Could not grant 'Log on as a service' to {account.DisplayName}: {detail}");
            }
        }

        // 3. Missing IIS features, then the hosting bundle (AFTER IIS, so its
        //    IIS module registers), then iisreset.
        var iisChanged = false;
        if (evaluation.MissingIisFeatures.Count > 0)
        {
            progress.Info($"> Install IIS features: {string.Join(", ", evaluation.MissingIisFeatures)}");
            actions.InstallIisFeatures(evaluation.MissingIisFeatures, evaluation.IsServerOs);
            iisChanged = true;
        }

        if (evaluation.HostingBundleRequired)
        {
            if (string.IsNullOrWhiteSpace(evaluation.HostingBundleInstallerPath))
            {
                throw new InvalidOperationException(
                    $"The ASP.NET Core Hosting Bundle for runtime {evaluation.RequiredRuntimeMajor} is required, but no matching dotnet-hosting installer was found in the package's prereqs folder.");
            }

            progress.Info(evaluation.HostingBundleRepairRequired
                ? $"> Repair ASP.NET Core Hosting Bundle ({Path.GetFileName(evaluation.HostingBundleInstallerPath)})"
                : $"> Install ASP.NET Core Hosting Bundle ({Path.GetFileName(evaluation.HostingBundleInstallerPath)})");
            var exitCode = actions.RunHostingBundle(evaluation.HostingBundleInstallerPath, evaluation.HostingBundleRepairRequired);
            switch (exitCode)
            {
                case 0:
                    break;
                case 3010:
                    progress.Info("  The hosting bundle asks for a restart (exit code 3010). The installation continues; restart the server when convenient.");
                    break;
                default:
                    throw new InvalidOperationException($"The hosting bundle installer failed with exit code {exitCode}.");
            }

            iisChanged = true;
        }

        if (iisChanged)
        {
            progress.Info("> Restart IIS");
            actions.RestartIis();
        }

        // 4. Apply the operator-typed password (incl. the app-pool fallback),
        //    then the shared install chain with progress routed to this UI.
        OperatorCredentialApplier.Apply(request.Config, request.OperatorPassword);
        var previousOutput = InstallOutput.Current;
        InstallOutput.Current = progress;
        try
        {
            await RunSharedInstallChainAsync(request, progress, cancellationToken);
        }
        finally
        {
            InstallOutput.Current = previousOutput;
        }

        // 5. Wait for the HostAgent's first successful sync, then probe the portal.
        var config = request.Config;
        if (config.HostAgent.Enabled && config.HostAgent.StartService)
        {
            var identity = InstallationEngine.ResolveBootstrapHostAgentServiceIdentity(config);
            progress.Info("> Wait for the HostAgent's first sync");
            var active = await actions.WaitForHostAgentActiveAsync(
                config.Sql.Server,
                config.Sql.Database,
                config.Sql.TrustServerCertificate,
                config.HostAgent.HostKey,
                identity.ServiceName,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                TimeSpan.FromMinutes(10),
                cancellationToken);
            if (!active)
            {
                throw new TimeoutException("The HostAgent did not report a successful first sync within 10 minutes. Check the HostAgent logs and the Windows service state.");
            }
        }

        var portalUrl = ProfileTargetResolver.ResolvePortalUrl(config);
        progress.Info($"> Probe the portal at {portalUrl}");
        var (portalOk, portalDetail) = await actions.ProbePortalAsync(portalUrl, TimeSpan.FromMinutes(5), cancellationToken);
        if (!portalOk)
        {
            throw new InvalidOperationException($"The portal at {portalUrl} did not answer: {portalDetail}");
        }

        progress.Info(string.Empty);
        progress.Info("Installation completed.");
    }

    /// <summary>
    /// The shared install chain, in the same order as the Bootstrapper's
    /// RunBootstrapAsync, minus CreateDatabaseIfConfiguredAsync: the database
    /// must already exist (verified by the prerequisite check).
    /// </summary>
    internal static async Task RunSharedInstallChainAsync(
        InstallRequest request,
        IInstallProgress progress,
        CancellationToken cancellationToken)
    {
        var config = request.Config;
        if (config.Sql.Enabled)
        {
            await InstallationEngine.RunSqlAsync(config, request.ConfigPath, request.PayloadRoot);
            await InstallationEngine.ImportModuleDefinitionsAsync(config, request.PayloadRoot);
            await InstallationEngine.EnsureRuntimeDatabaseAccessAsync(config);
        }

        var preparedConfigurationFiles = InstallationEngine.PrepareArtifacts(
            config,
            request.ConfigPath,
            request.PayloadRoot,
            ArtifactPreparationMode.InstallOrUpdate);
        await InstallationEngine.RegisterPackageArtifactsAsync(config);
        await InstallationEngine.RegisterPreparedArtifactConfigurationFilesAsync(config, preparedConfigurationFiles);
        await InstallationEngine.CopyMissingArtifactConfigurationFilesFromPreviousVersionsAsync(
            config,
            preparedConfigurationFiles);
        InstallationEngine.PublishAvailableDeploymentObjects(config, request.PayloadRoot);

        if (config.HostAgent.Enabled)
        {
            InstallationEngine.WriteHostAgentInstallOrUpdateIntent(config);
            InstallationEngine.EnsureRuntimeFilesystemAccess(config);
            await InstallationEngine.InstallHostAgentAsync(config, request.PayloadRoot);
        }
    }

    /// <summary>What <c>--dry-run</c> prints: the same decisions, as "would" lines.</summary>
    internal static void ReportPlan(InstallRequest request, IInstallProgress progress)
    {
        var evaluation = request.Evaluation;
        var config = request.Config;

        progress.Info("Dry run - nothing will be changed.");
        progress.Info(string.Empty);
        foreach (var check in evaluation.Checks)
        {
            progress.Info($"[{(check.IsSatisfied ? "OK" : "MISSING")}] {check.Title}: {check.Detail}");
        }

        progress.Info(string.Empty);
        if (evaluation.ServiceAccount.RequiresPassword)
        {
            progress.Info($"Would verify the password for {evaluation.ServiceAccount.DisplayName} and grant it 'Log on as a service'.");
        }

        if (evaluation.MissingIisFeatures.Count > 0)
        {
            progress.Info($"Would install IIS features ({(evaluation.IsServerOs ? "Install-WindowsFeature" : "Enable-WindowsOptionalFeature")}): {string.Join(", ", evaluation.MissingIisFeatures)}.");
        }

        if (evaluation.HostingBundleRequired)
        {
            progress.Info(evaluation.HostingBundleInstallerPath is null
                ? $"Would need the .NET {evaluation.RequiredRuntimeMajor} hosting bundle, but no matching installer was found in prereqs."
                : $"Would {(evaluation.HostingBundleRepairRequired ? "repair" : "install")} the ASP.NET Core Hosting Bundle from {Path.GetFileName(evaluation.HostingBundleInstallerPath)}.");
        }

        if (evaluation.MissingIisFeatures.Count > 0 || evaluation.HostingBundleRequired)
        {
            progress.Info("Would restart IIS (iisreset).");
        }

        if (config.Sql.Enabled)
        {
            progress.Info($"Would run the profile's SQL scripts against {config.Sql.Server}/{config.Sql.Database} (the database already exists and is never created), import module definitions, and ensure runtime database access.");
        }

        progress.Info($"Would prepare {config.Artifacts.Count(artifact => artifact.Enabled)} artifact(s) into {config.ArtifactStoreRoot}, register them, and publish deployment objects.");
        if (config.HostAgent.Enabled)
        {
            progress.Info($"Would install the HostAgent service ({config.HostAgent.ServiceName}) under {config.HostAgent.InstallPath}, wait for its first sync, and probe {ProfileTargetResolver.ResolvePortalUrl(config)}.");
        }
    }
}
