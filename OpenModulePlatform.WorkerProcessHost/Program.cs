// File: OpenModulePlatform.WorkerProcessHost/Program.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NLog.Extensions.Hosting;
using OpenModulePlatform.WorkerProcessHost.Models;
using OpenModulePlatform.WorkerProcessHost.Plugins;
using OpenModulePlatform.WorkerProcessHost.Runtime;
using OpenModulePlatform.WorkerProcessHost.Services;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
    })
    .UseNLog()
    .ConfigureServices((context, services) =>
    {
        services.AddSingleton<IValidateOptions<WorkerProcessSettings>, WorkerProcessSettingsValidator>();
        services.AddOptions<WorkerProcessSettings>()
            .Bind(context.Configuration.GetSection(WorkerProcessSettings.SectionName))
            .ValidateOnStart();
        services.AddSingleton<WorkerModuleLoader>();
        services.AddSingleton<WorkerRuntimeContextFactory>();
        services.AddHostedService<WorkerProcessHostedService>();
    });

await builder.Build().RunAsync();
