using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Hosting;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.HostAgent.WindowsService.Services;

var runOnce = args.Any(static arg => string.Equals(arg, "--run-once", StringComparison.OrdinalIgnoreCase));
var hostArgs = args
    .Where(static arg => !string.Equals(arg, "--run-once", StringComparison.OrdinalIgnoreCase))
    .ToArray();

var builder = Host.CreateDefaultBuilder(hostArgs)
    .UseWindowsService(options =>
    {
        options.ServiceName = "OpenModulePlatform.HostAgent.WindowsService";
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .UseNLog()
    .ConfigureServices((context, services) =>
    {
        services.Configure<HostAgentSettings>(context.Configuration.GetSection("HostAgent"));
        services.AddSingleton<SqlConnectionFactory>();
        services.AddSingleton<OmpHostArtifactRepository>();
        services.AddSingleton<ArtifactProvisioner>();
        services.AddSingleton<ArtifactZipImportService>();
        services.AddSingleton<WebAppDeploymentService>();
        services.AddSingleton<ServiceAppDeploymentService>();
        services.AddSingleton<HostAgentFileMirrorService>();
        services.AddSingleton<HostAgentEngine>();
        services.AddHostedService<HostAgentHostedService>();
        services.AddHostedService<HostAgentRpcHostedService>();
    });

using var host = builder.Build();
if (runOnce)
{
    var engine = host.Services.GetRequiredService<HostAgentEngine>();
    await engine.RunOnceAsync(CancellationToken.None);
    return;
}

await host.RunAsync();
