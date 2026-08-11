// File: OpenModulePlatform.WorkerProcessHost/Program.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NLog;
using NLog.Extensions.Hosting;
using OpenModulePlatform.WorkerProcessHost.Models;
using OpenModulePlatform.WorkerProcessHost.Plugins;
using OpenModulePlatform.WorkerProcessHost.Runtime;
using OpenModulePlatform.WorkerProcessHost.Services;

// Artifact payloads deliberately strip appsettings.json (configuration must
// never change the artifact hash), which silently removed the NLog config -
// worker plugin logs went nowhere, making worker-side failures invisible.
// The file logging is therefore configured in code as the always-on default;
// an NLog section in configuration (config overlay) still takes precedence
// through UseNLog() below.
// The log directory must live OUTSIDE the artifact folder: HostAgent validates
// the provisioned artifact against its SHA manifest, so files written under
// basedir turn every validation into a repair attempt that then fails on the
// open log handle.
static void ConfigureDefaultNLog()
{
    if (LogManager.Configuration is not null)
    {
        return;
    }

    var config = new NLog.Config.LoggingConfiguration();
    var logfile = new NLog.Targets.FileTarget("logfile")
    {
        FileName = "${specialfolder:folder=CommonApplicationData}/OpenModulePlatform/logs/OpenModulePlatform.WorkerProcessHost-${shortdate}.log",
        Layout = "${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner= ${exception:format=tostring}}",
        MaxArchiveDays = 14
    };
    var console = new NLog.Targets.ConsoleTarget("console")
    {
        Layout = "${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner= ${exception:format=tostring}}"
    };
    config.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, logfile);
    config.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, console);
    LogManager.Configuration = config;
}

ConfigureDefaultNLog();

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
