// File: OpenModulePlatform.Worker.ExampleWorkerAppModule/ExampleWorkerAppModule.cs
using OpenModulePlatform.Worker.Abstractions.Contracts;
using OpenModulePlatform.Worker.Abstractions.Models;
using OpenModulePlatform.Worker.ExampleWorkerAppModule.Services;

namespace OpenModulePlatform.Worker.ExampleWorkerAppModule;

/// <summary>
/// Sample worker module for the manager-driven worker runtime.
/// </summary>
public sealed class ExampleWorkerAppModule : IWorkerModule
{
    private readonly ExampleWorkerAppModuleWorkerEngine _engine;

    public ExampleWorkerAppModule(ExampleWorkerAppModuleWorkerEngine engine)
    {
        _engine = engine;
    }

    public Task RunAsync(WorkerExecutionContext context, CancellationToken cancellationToken)
        => _engine.ExecuteLoopAsync(context, cancellationToken);
}
