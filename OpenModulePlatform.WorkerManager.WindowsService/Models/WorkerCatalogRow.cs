// File: OpenModulePlatform.WorkerManager.WindowsService/Models/WorkerCatalogRow.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

/// <summary>
/// The raw fields of one OMP database worker catalog row, before any validation
/// (R7-F6). Keeping the row separate from the validated
/// <see cref="DesiredWorkerInstance"/> is what lets the catalog skip a single broken
/// row instead of failing the host's whole reconciliation.
/// </summary>
public sealed record WorkerCatalogRow
{
    public required Guid AppInstanceId { get; init; }

    public required Guid WorkerInstanceId { get; init; }

    public required string WorkerInstanceKey { get; init; }

    public required string WorkerTypeKey { get; init; }

    public int? ArtifactId { get; init; }

    public string? PackageType { get; init; }

    public string? InstallPath { get; init; }

    public bool IsProvisionedFromHostArtifactCache { get; init; }

    public required string PluginRelativePath { get; init; }

    public string? ConfigurationJson { get; init; }

    public string? ArtifactVersion { get; init; }
}
