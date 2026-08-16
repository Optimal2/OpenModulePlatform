// File: OpenModulePlatform.WorkerManager.WindowsService/Models/DesiredWorkerInstance.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

public sealed class DesiredWorkerInstance
{
    public Guid AppInstanceId { get; init; }

    public Guid WorkerInstanceId { get; init; }

    public string WorkerInstanceKey { get; init; } = string.Empty;

    public string WorkerTypeKey { get; init; } = string.Empty;

    public int? ArtifactId { get; init; }

    /// <summary>
    /// The catalogued version of <see cref="ArtifactId"/> as it read when this definition
    /// was resolved (R12-F2).
    /// </summary>
    /// <remarks>
    /// This is what the manager publishes as the runtime version witness once a process
    /// has actually been started from the definition. It is carried on the definition
    /// rather than looked up at publish time on purpose: a running worker keeps the
    /// definition it was started with until it is restarted, so the definition is the only
    /// place the STARTED version still exists after the catalogue has moved on -- which is
    /// exactly the case the witness exists to expose.
    ///
    /// Measured before choosing this source: the plugin assembly's own file version is not
    /// the artifact version and cannot stand in for it. ibs-packager artifact 0.3.109 ships
    /// IbsPackager.Worker.dll with FileVersion 0.3.115.0 and ProductVersion 0.3.43+sha.
    /// </remarks>
    public string? ArtifactVersion { get; init; }

    public string? InstallRootPath { get; init; }

    public bool IsProvisionedFromHostArtifactCache { get; init; }

    public string PluginRelativePath { get; init; } = string.Empty;

    public string PluginAssemblyPath { get; init; } = string.Empty;

    public string? ConfigurationJson { get; init; }

    public string ShutdownEventName { get; init; } = string.Empty;

    public bool HasEquivalentConfiguration(DesiredWorkerInstance other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return AppInstanceId == other.AppInstanceId
            && WorkerInstanceId == other.WorkerInstanceId
            && string.Equals(WorkerInstanceKey, other.WorkerInstanceKey, StringComparison.Ordinal)
            && string.Equals(WorkerTypeKey, other.WorkerTypeKey, StringComparison.Ordinal)
            && ArtifactId == other.ArtifactId
            // R12-F2. A version rewritten in place under an unchanged ArtifactId is still a
            // version change, and the running process would otherwise keep reporting the
            // version it started with forever -- correct as a witness, but a drift the gate
            // reports and nothing ever clears. Comparing it here makes the restart the fix.
            && string.Equals(ArtifactVersion, other.ArtifactVersion, StringComparison.Ordinal)
            && string.Equals(InstallRootPath, other.InstallRootPath, StringComparison.Ordinal)
            && IsProvisionedFromHostArtifactCache == other.IsProvisionedFromHostArtifactCache
            && string.Equals(PluginRelativePath, other.PluginRelativePath, StringComparison.Ordinal)
            && string.Equals(PluginAssemblyPath, other.PluginAssemblyPath, StringComparison.Ordinal)
            && string.Equals(ConfigurationJson, other.ConfigurationJson, StringComparison.Ordinal)
            && string.Equals(ShutdownEventName, other.ShutdownEventName, StringComparison.Ordinal);
    }

    public DesiredWorkerInstance WithInstallRootPath(string installRootPath)
    {
        if (string.IsNullOrWhiteSpace(installRootPath))
        {
            return this;
        }

        return new DesiredWorkerInstance
        {
            AppInstanceId = AppInstanceId,
            WorkerInstanceId = WorkerInstanceId,
            WorkerInstanceKey = WorkerInstanceKey,
            WorkerTypeKey = WorkerTypeKey,
            ArtifactId = ArtifactId,
            // Must be carried through: the resolved definition is compared against the
            // running one by HasEquivalentConfiguration, so dropping it here would make
            // every cycle look like a configuration change and restart every worker.
            ArtifactVersion = ArtifactVersion,
            InstallRootPath = installRootPath,
            IsProvisionedFromHostArtifactCache = true,
            PluginRelativePath = PluginRelativePath,
            PluginAssemblyPath = ResolvePluginAssemblyPath(installRootPath, PluginRelativePath),
            ConfigurationJson = ConfigurationJson,
            ShutdownEventName = ShutdownEventName
        };
    }

    public static string ResolvePluginAssemblyPath(string installRootPath, string pluginRelativePath)
    {
        // PluginRelativePath comes from the OMP database (omp.AppWorkerDefinitions),
        // so it is untrusted input. Without the same rooted-rejection and
        // containment check the DB catalog path enforces
        // (OmpDatabaseWorkerInstanceCatalog.ResolvePluginAssemblyPath), a value
        // like "..\..\Windows\System32\evil.dll" escapes the install root and
        // would be loaded and executed as the worker service account (R3-C1).
        if (Path.IsPathRooted(pluginRelativePath.Trim()))
        {
            throw new InvalidOperationException(
                $"PluginRelativePath '{pluginRelativePath}' is rooted; it must be relative to the artifact install path.");
        }

        var installRoot = Path.GetFullPath(installRootPath.Trim());
        var normalizedInstallRoot = installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sanitizedRelativePath = pluginRelativePath.Trim()
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var candidatePath = string.IsNullOrWhiteSpace(sanitizedRelativePath)
            ? Path.GetFullPath(normalizedInstallRoot)
            : Path.GetFullPath($"{normalizedInstallRoot}{Path.DirectorySeparatorChar}{sanitizedRelativePath}");

        var normalizedRoot = normalizedInstallRoot + Path.DirectorySeparatorChar;
        if (!candidatePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidatePath, installRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"PluginRelativePath '{pluginRelativePath}' resolves outside the install root '{installRootPath}'.");
        }

        return candidatePath;
    }
}
