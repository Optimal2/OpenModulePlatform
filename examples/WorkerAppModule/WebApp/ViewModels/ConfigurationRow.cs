// File: OpenModulePlatform.Web.ExampleWorkerAppModule/ViewModels/ConfigurationRow.cs
using OpenModulePlatform.Web.Shared.Configuration;

namespace OpenModulePlatform.Web.ExampleWorkerAppModule.ViewModels;

public sealed class ConfigurationRow
{
    public ModuleConfigId ConfigId { get; set; }
    public int VersionNo { get; set; }
    public string ConfigJson { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string? CreatedBy { get; set; }
    public int AssignedInstallations { get; set; }
}
