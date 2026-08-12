// File: OpenModulePlatform.WorkerManager.WindowsService/Models/DesiredWorkerInstance.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

public sealed class DesiredWorkerInstance
{
    public Guid AppInstanceId { get; init; }

    public Guid WorkerInstanceId { get; init; }

    public string WorkerInstanceKey { get; init; } = string.Empty;

    public string WorkerTypeKey { get; init; } = string.Empty;

    public int? ArtifactId { get; init; }

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
