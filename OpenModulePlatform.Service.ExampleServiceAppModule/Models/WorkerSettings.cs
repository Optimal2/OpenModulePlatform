// File: OpenModulePlatform.Service.ExampleServiceAppModule/Models/WorkerSettings.cs
namespace OpenModulePlatform.Service.ExampleServiceAppModule.Models;

public sealed class WorkerSettings
{
    public Guid AppInstanceId { get; set; }
    public int PollSeconds { get; set; } = 5;
    public int ConfigRefreshSeconds { get; set; } = 15;
    public int HeartbeatSeconds { get; set; } = 10;
}
