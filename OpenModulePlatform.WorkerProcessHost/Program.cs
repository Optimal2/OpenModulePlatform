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
// The file logging is therefore configured in code, and this configuration is
// the one that runs: it is set before the host is built, and UseNLog() below
// does not replace a configuration that is already set. Measured 2026-10-13:
// started from a build output that HAS appsettings.json, with the NLog
// variable appName overridden through the environment, every line (before and
// after the host was built) went to the file named here and none to the
// appsettings name. The NLog section in appsettings.json is kept in step with
// this file name but is not what a running host uses.
//
// Every worker process writes its OWN file: ${processid} is part of the name.
// WorkerManager starts one host process per worker instance, all from the same
// provisioned folder, so they share the log directory. With one shared file
// per day only the process that opened it first wrote anything; every other
// process lost its lines silently (measured 2026-10-13 on one installation:
// 36 worker process starts, 2 "Worker process host starting" lines, and no
// line from any worker of one module in four days of files). The first line
// of each file names the worker instance it belongs to.
//
// The default log directory has to satisfy two rules at once.
//
// It must live OUTSIDE the artifact folder: HostAgent validates the provisioned
// artifact against its SHA manifest, so files written under basedir turn every
// validation into a repair attempt that then fails on the open log handle.
//
// It must ALSO stay on the drive the worker host was provisioned onto. The
// default used to be CommonApplicationData (C:\ProgramData), which is wrong on
// every installation that keeps the platform off the system drive. At one such
// installation that folder does not exist and must not be created, so a worker that threw during
// startup logged NOWHERE: the process exited 1 every fifteen seconds and the
// only trace was WorkerManager observing the exit code. Measured 2026-08-23 on
// a module's workers in production.
//
// A sibling of the artifact folder satisfies both: outside basedir, and on
// whichever drive HostAgent put the worker host on (D:\Services\WorkerProcessHost
// becomes D:\Services\Logs\WorkerProcessHost).
static string? EnsureDefaultLogDirectory()
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

    var logDirectory = EnsureDefaultLogDirectory();
    if (logDirectory is not null)
    {
        WorkerHostLogFile.DeleteExpiredFiles(logDirectory, DateTime.UtcNow, TimeSpan.FromDays(WorkerHostLogFile.RetentionDays));
        var logfile = new NLog.Targets.FileTarget("logfile")
        {
            FileName = Path.Combine(logDirectory, WorkerHostLogFile.DefaultFileName),
            Layout = layout,
            MaxArchiveDays = WorkerHostLogFile.RetentionDays
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

// Name the directory this host actually runs from, on the host's own first log line.
// During the 2026-08-23 worker-host incident the only way to tie a running process to
// a host build was the MANAGER's log; the host itself never said where it was loaded
// from. The provisioned directory is versioned, so this one line answers both
// "which build" and "which copy".
LogManager.GetCurrentClassLogger().Info(
    "Worker process host starting. BaseDirectory={0}, WorkerInstanceKey={1}",
    AppContext.BaseDirectory, WorkerHostLogFile.WorkerInstanceKeyFromArgs(args));

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
