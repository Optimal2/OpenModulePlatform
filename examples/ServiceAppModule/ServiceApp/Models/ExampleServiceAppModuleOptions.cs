// File: OpenModulePlatform.Service.ExampleServiceAppModule/Models/ExampleServiceAppModuleOptions.cs
namespace OpenModulePlatform.Service.ExampleServiceAppModule.Models;

public sealed class ExampleServiceAppModuleOptions
{
    public int ScanBatchSize { get; set; } = 1;
    public bool SampleMode { get; set; } = true;
}
