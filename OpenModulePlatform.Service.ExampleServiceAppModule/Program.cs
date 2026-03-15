// File: OpenModulePlatform.Service.ExampleServiceAppModule/Program.cs
using OpenModulePlatform.Service.ExampleServiceAppModule.Models;
using OpenModulePlatform.Service.ExampleServiceAppModule.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "OpenModulePlatform.Service.ExampleServiceAppModule";
    })
    .ConfigureLogging(logging =>
    {
        if (OperatingSystem.IsWindows())
            logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Error);
    })
    .ConfigureServices((context, services) =>
    {
        services.Configure<WorkerSettings>(context.Configuration.GetSection("Worker"));

        services.AddSingleton<SqlConnectionFactory>();
        services.AddSingleton<HostInstallationRepository>();
        services.AddSingleton<ExampleServiceAppModuleConfigurationRepository>();
        services.AddSingleton<ExampleServiceAppModuleConfigService>();
        services.AddSingleton<ExampleServiceAppModuleJobRepository>();
        services.AddSingleton<ExampleServiceAppModuleJobProcessor>();
        services.AddSingleton<ExampleServiceAppModuleWorkerEngine>();
        services.AddHostedService<ExampleServiceAppModuleDbWorkerService>();
    });

await builder.Build().RunAsync();
