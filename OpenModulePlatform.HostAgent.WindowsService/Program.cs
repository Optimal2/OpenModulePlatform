using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Hosting;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.HostAgent.WindowsService.Services;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "OpenModulePlatform.HostAgent.WindowsService";
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
    })
    .UseNLog()
    .ConfigureServices((context, services) =>
    {
        services.Configure<HostAgentSettings>(context.Configuration.GetSection("HostAgent"));
        services.AddSingleton<SqlConnectionFactory>();
        services.AddSingleton<OmpHostArtifactRepository>();
        services.AddSingleton<ArtifactProvisioner>();
        services.AddSingleton<HostAgentEngine>();
        services.AddHostedService<HostAgentHostedService>();
        services.AddHostedService<HostAgentRpcHostedService>();
    });

await builder.Build().RunAsync();
