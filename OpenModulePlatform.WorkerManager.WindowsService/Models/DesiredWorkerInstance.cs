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
        var installRoot = Path.GetFullPath(installRootPath.Trim());
        var normalizedInstallRoot = installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sanitizedRelativePath = pluginRelativePath.Trim()
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.IsNullOrWhiteSpace(sanitizedRelativePath)
            ? Path.GetFullPath(normalizedInstallRoot)
            : Path.GetFullPath($"{normalizedInstallRoot}{Path.DirectorySeparatorChar}{sanitizedRelativePath}");
    }
}
