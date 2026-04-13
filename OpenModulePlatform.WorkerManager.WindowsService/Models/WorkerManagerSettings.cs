// File: OpenModulePlatform.WorkerManager.WindowsService/Models/WorkerManagerSettings.cs
namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

public sealed class WorkerManagerSettings
{
    public int RefreshSeconds { get; set; } = 15;
}
