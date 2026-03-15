// File: OpenModulePlatform.Web.ExampleServiceAppModule/ViewModels/OverviewRow.cs
namespace OpenModulePlatform.Web.ExampleServiceAppModule.ViewModels;

public sealed class OverviewRow
{
    public string ModuleKey { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public int ActiveConfigurationCount { get; set; }
    public int HostInstallationCount { get; set; }
    public int OpenJobCount { get; set; }
}
