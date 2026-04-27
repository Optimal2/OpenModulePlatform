// File: OpenModulePlatform.Service.ExampleServiceAppModule/Services/ExampleServiceAppModuleDbWorkerService.cs
namespace OpenModulePlatform.Service.ExampleServiceAppModule.Services;

public sealed class ExampleServiceAppModuleDbWorkerService : BackgroundService
{
    private readonly ExampleServiceAppModuleWorkerEngine _engine;

    public ExampleServiceAppModuleDbWorkerService(ExampleServiceAppModuleWorkerEngine engine)
    {
        _engine = engine;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => _engine.ExecuteLoopAsync(stoppingToken);
}
