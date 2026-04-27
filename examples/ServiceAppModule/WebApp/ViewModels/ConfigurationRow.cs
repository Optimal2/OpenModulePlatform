// File: OpenModulePlatform.Web.ExampleServiceAppModule/ViewModels/ConfigurationRow.cs
namespace OpenModulePlatform.Web.ExampleServiceAppModule.ViewModels;

public sealed class ConfigurationRow
{
    public int ConfigId { get; set; }
    public int VersionNo { get; set; }
    public string ConfigJson { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? CreatedBy { get; set; }
    public int AssignedInstallations { get; set; }
}
