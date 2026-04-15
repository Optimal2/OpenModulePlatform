// File: OpenModulePlatform.Worker.ExampleWorkerAppModule/ExampleWorkerAppModuleFactory.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Worker.Abstractions.Contracts;
using OpenModulePlatform.Worker.ExampleWorkerAppModule.Services;

namespace OpenModulePlatform.Worker.ExampleWorkerAppModule;

/// <summary>
/// Creates the sample worker module for the example worker-backed module.
/// </summary>
public sealed class ExampleWorkerAppModuleFactory : IWorkerModuleFactory
{
    public string WorkerTypeKey => "omp.example.workerapp_module";

    public IWorkerModule Create(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        var db = new SqlConnectionFactory(configuration);
        var appInstances = new AppInstanceRepository(db);
        var configurationRepository = new ExampleWorkerAppModuleConfigurationRepository(db);
        var configService = new ExampleWorkerAppModuleConfigService(appInstances, configurationRepository);
        var jobRepository = new ExampleWorkerAppModuleJobRepository(db);
        var jobProcessor = new ExampleWorkerAppModuleJobProcessor(
            loggerFactory.CreateLogger<ExampleWorkerAppModuleJobProcessor>(),
            jobRepository);
        var engine = new ExampleWorkerAppModuleWorkerEngine(
            loggerFactory.CreateLogger<ExampleWorkerAppModuleWorkerEngine>(),
            configService,
            jobRepository,
            jobProcessor);

        return new ExampleWorkerAppModule(engine);
    }
}
