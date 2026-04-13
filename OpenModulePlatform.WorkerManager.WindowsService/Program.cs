// File: OpenModulePlatform.WorkerManager.WindowsService/Program.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Hosting;
using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Services;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "OpenModulePlatform.WorkerManager.WindowsService";
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
    })
    .UseNLog()
    .ConfigureServices((context, services) =>
    {
        services.Configure<WorkerManagerSettings>(context.Configuration.GetSection("WorkerManager"));
        services.AddHostedService<WorkerManagerHostedService>();
    });

await builder.Build().RunAsync();
