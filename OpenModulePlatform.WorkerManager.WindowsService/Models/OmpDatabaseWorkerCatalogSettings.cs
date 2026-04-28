// File: OpenModulePlatform.WorkerManager.WindowsService/Models/OmpDatabaseWorkerCatalogSettings.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

public sealed class OmpDatabaseWorkerCatalogSettings
{
    public string RuntimeKind { get; set; } = "windows-worker-plugin";

    public byte RunningDesiredState { get; set; } = 1;

    public bool UseHostArtifactCache { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RuntimeKind))
        {
            throw new InvalidOperationException("WorkerManager:OmpDatabase:RuntimeKind must be configured.");
        }
    }
}
