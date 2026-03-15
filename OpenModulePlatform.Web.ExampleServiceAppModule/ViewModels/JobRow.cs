// File: OpenModulePlatform.Web.ExampleServiceAppModule/ViewModels/JobRow.cs
namespace OpenModulePlatform.Web.ExampleServiceAppModule.ViewModels;

public sealed class JobRow
{
    public long JobId { get; set; }
    public string RequestType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public byte Status { get; set; }
    public int Attempts { get; set; }
    public DateTime RequestedUtc { get; set; }
    public string? RequestedBy { get; set; }
    public string? LastError { get; set; }
    public string? ResultJson { get; set; }
}
