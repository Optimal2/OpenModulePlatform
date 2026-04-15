// File: OpenModulePlatform.WorkerManager.WindowsService/Models/WorkerManagerSettings.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

public sealed class WorkerManagerSettings
{
    public string CatalogMode { get; set; } = WorkerCatalogModes.Configuration;

    public string? HostKey { get; set; }

    public string? HostName { get; set; }

    public int RefreshSeconds { get; set; } = 15;

    public string WorkerProcessPath { get; set; } = string.Empty;

    public int StopTimeoutSeconds { get; set; } = 15;

    public int RestartDelaySeconds { get; set; } = 5;

    public int RestartWindowSeconds { get; set; } = 300;

    public int MaxRestartsPerWindow { get; set; } = 5;

    public OmpDatabaseWorkerCatalogSettings OmpDatabase { get; set; } = new();

    public List<ConfiguredWorkerInstance> Workers { get; set; } = new();

    public string GetCatalogMode()
    {
        return string.IsNullOrWhiteSpace(CatalogMode)
            ? WorkerCatalogModes.Configuration
            : CatalogMode.Trim();
    }

    public string ResolveHostKey()
    {
        if (!string.IsNullOrWhiteSpace(HostKey))
        {
            return HostKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(HostName))
        {
            return HostName.Trim();
        }

        return Environment.MachineName;
    }

    public void Validate()
    {
        var catalogMode = GetCatalogMode();
        if (!string.Equals(catalogMode, WorkerCatalogModes.Configuration, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(catalogMode, WorkerCatalogModes.OmpDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"WorkerManager:CatalogMode must be '{WorkerCatalogModes.Configuration}' or '{WorkerCatalogModes.OmpDatabase}'.");
        }

        if (RefreshSeconds < 1)
        {
            throw new InvalidOperationException("WorkerManager:RefreshSeconds must be at least 1.");
        }

        if (StopTimeoutSeconds < 1)
        {
            throw new InvalidOperationException("WorkerManager:StopTimeoutSeconds must be at least 1.");
        }

        if (RestartDelaySeconds < 0)
        {
            throw new InvalidOperationException("WorkerManager:RestartDelaySeconds cannot be negative.");
        }

        if (RestartWindowSeconds < 1)
        {
            throw new InvalidOperationException("WorkerManager:RestartWindowSeconds must be at least 1.");
        }

        if (MaxRestartsPerWindow < 1)
        {
            throw new InvalidOperationException("WorkerManager:MaxRestartsPerWindow must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(WorkerProcessPath))
        {
            throw new InvalidOperationException("WorkerManager:WorkerProcessPath must be configured.");
        }

        if (string.Equals(catalogMode, WorkerCatalogModes.OmpDatabase, StringComparison.OrdinalIgnoreCase))
        {
            OmpDatabase.Validate();
        }
    }
}
