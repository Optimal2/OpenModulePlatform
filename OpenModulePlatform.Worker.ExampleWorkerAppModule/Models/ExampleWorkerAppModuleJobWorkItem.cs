// File: OpenModulePlatform.Worker.ExampleWorkerAppModule/Models/ExampleWorkerAppModuleJobWorkItem.cs
namespace OpenModulePlatform.Worker.ExampleWorkerAppModule.Models;

public sealed class ExampleWorkerAppModuleJobWorkItem
{
    public long JobId { get; set; }
    public string RequestType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime RequestedUtc { get; set; }
    public string? RequestedBy { get; set; }
}
