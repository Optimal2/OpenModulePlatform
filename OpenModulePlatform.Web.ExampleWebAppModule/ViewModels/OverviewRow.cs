// File: OpenModulePlatform.Web.ExampleWebAppModule/ViewModels/OverviewRow.cs
namespace OpenModulePlatform.Web.ExampleWebAppModule.ViewModels;

public sealed class OverviewRow
{
    public string ModuleKey { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public int ActiveConfigurationCount { get; set; }
}
