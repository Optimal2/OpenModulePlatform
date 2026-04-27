using Microsoft.Extensions.Logging;
// File: OpenModulePlatform.Service.ExampleServiceAppModule/Program.cs
using OpenModulePlatform.Service.ExampleServiceAppModule.Models;
using OpenModulePlatform.Service.ExampleServiceAppModule.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using NLog.Extensions.Hosting;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "OpenModulePlatform.Service.ExampleServiceAppModule";
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
    })
    .UseNLog()
    .ConfigureServices((context, services) =>
    {
        services.Configure<WorkerSettings>(context.Configuration.GetSection("Worker"));

        services.AddSingleton<SqlConnectionFactory>();
        services.AddSingleton<AppInstanceRepository>();
        services.AddSingleton<ExampleServiceAppModuleConfigurationRepository>();
        services.AddSingleton<ExampleServiceAppModuleConfigService>();
        services.AddSingleton<ExampleServiceAppModuleJobRepository>();
        services.AddSingleton<ExampleServiceAppModuleJobProcessor>();
        services.AddSingleton<ExampleServiceAppModuleWorkerEngine>();
        services.AddHostedService<ExampleServiceAppModuleDbWorkerService>();
    });

await builder.Build().RunAsync();
