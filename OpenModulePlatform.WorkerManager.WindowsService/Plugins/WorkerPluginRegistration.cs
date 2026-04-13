// File: OpenModulePlatform.WorkerManager.WindowsService/Plugins/WorkerPluginRegistration.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Plugins;

/// <summary>
/// Describes a worker plugin registration known to the manager.
/// </summary>
public sealed class WorkerPluginRegistration
{
    /// <summary>
    /// Gets or sets the stable worker type key.
    /// </summary>
    public string WorkerTypeKey { get; init; } = string.Empty;
}
