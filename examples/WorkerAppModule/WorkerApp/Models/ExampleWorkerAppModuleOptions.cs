// File: OpenModulePlatform.Worker.ExampleWorkerAppModule/Models/ExampleWorkerAppModuleOptions.cs
namespace OpenModulePlatform.Worker.ExampleWorkerAppModule.Models;

public sealed class ExampleWorkerAppModuleOptions
{
    public int ScanBatchSize { get; set; } = 1;
    public bool SampleMode { get; set; } = true;
}
