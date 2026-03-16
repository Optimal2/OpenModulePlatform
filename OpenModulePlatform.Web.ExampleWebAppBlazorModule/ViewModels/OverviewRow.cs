// File: OpenModulePlatform.Web.ExampleWebAppBlazorModule/ViewModels/OverviewRow.cs
namespace OpenModulePlatform.Web.ExampleWebAppBlazorModule.ViewModels;

public sealed class OverviewRow
{
    public string ModuleKey { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public int ActiveConfigurationCount { get; set; }
}
