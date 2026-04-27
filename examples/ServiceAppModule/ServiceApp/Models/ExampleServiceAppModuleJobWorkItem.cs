// File: OpenModulePlatform.Service.ExampleServiceAppModule/Models/ExampleServiceAppModuleJobWorkItem.cs
namespace OpenModulePlatform.Service.ExampleServiceAppModule.Models;

public sealed class ExampleServiceAppModuleJobWorkItem
{
    public long JobId { get; set; }
    public string RequestType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime RequestedUtc { get; set; }
    public string? RequestedBy { get; set; }
}
