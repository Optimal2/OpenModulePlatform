// File: OpenModulePlatform.Web.ExampleWorkerAppModule/ViewModels/OverviewRow.cs
namespace OpenModulePlatform.Web.ExampleWorkerAppModule.ViewModels;

public sealed class OverviewRow
{
    public string ModuleKey { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public int ActiveConfigurationCount { get; set; }
    public int WorkerAppInstanceCount { get; set; }
    public int RunningWorkerCount { get; set; }
    public int OpenJobCount { get; set; }
}
