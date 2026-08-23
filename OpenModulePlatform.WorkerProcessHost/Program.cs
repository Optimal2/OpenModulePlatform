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
//
// The default log directory has to satisfy two rules at once.
//
// It must live OUTSIDE the artifact folder: HostAgent validates the provisioned
// artifact against its SHA manifest, so files written under basedir turn every
// validation into a repair attempt that then fails on the open log handle.
//
// It must ALSO stay on the drive the worker host was provisioned onto. The
// default used to be CommonApplicationData (C:\ProgramData), which is wrong on
// every installation that keeps the platform off the system drive. At VGR that
// folder does not exist and must not be created, so a worker that threw during
// startup logged NOWHERE: the process exited 1 every fifteen seconds and the
// only trace was WorkerManager observing the exit code. Measured 2026-08-23 on
// the IbsPackager workers in production.
//
// A sibling of the artifact folder satisfies both: outside basedir, and on
// whichever drive HostAgent put the worker host on (D:\Services\WorkerProcessHost
// becomes D:\Services\Logs\WorkerProcessHost).
static string? ResolveDefaultLogDirectory()
{
    try
    {
        var basedir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        var parent = Directory.GetParent(basedir);
        if (parent is null)
        {
            return null;
        }

        var directory = Path.Combine(parent.FullName, "Logs", "WorkerProcessHost");
        Directory.CreateDirectory(directory);
        return directory;
    }
    catch (Exception)
    {
        // Deliberately NO fallback onto the system drive. An installation that
        // keeps the platform off C: does so for a reason, and silently writing
        // there instead would be worse than not writing a file at all - the
        // console target below still carries the failure to whoever is running
        // the manager interactively.
        return null;
    }
}

static void ConfigureDefaultNLog()
{
    if (LogManager.Configuration is not null)
    {
        return;
    }

    const string layout = "${longdate}|${uppercase:${level}}|${logger}|${message}${onexception:inner= ${exception:format=tostring}}";

    var config = new NLog.Config.LoggingConfiguration();

    var logDirectory = ResolveDefaultLogDirectory();
    if (logDirectory is not null)
    {
        var logfile = new NLog.Targets.FileTarget("logfile")
        {
            FileName = Path.Combine(logDirectory, "OpenModulePlatform.WorkerProcessHost-${shortdate}.log"),
            Layout = layout,
            MaxArchiveDays = 14
        };
        config.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, logfile);
    }

    var console = new NLog.Targets.ConsoleTarget("console")
    {
        Layout = layout
    };
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
