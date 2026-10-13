using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Worker.Abstractions.Contracts;
using OpenModulePlatform.Worker.Abstractions.Models;
using OpenModulePlatform.WorkerProcessHost.Runtime;

namespace OpenModulePlatform.WorkerProcessHost.Tests;

/// <summary>
/// The worker process host's own log files. WorkerManager starts one host
/// process per worker instance from the same folder, so they share the log
/// directory; with one shared file per day only the first process wrote
/// anything (measured 2026-10-13: 36 worker process starts, 2 host start lines,
/// no line from any worker of one module in four days). These tests start REAL host
/// processes from a copy of this test's output folder (which carries the host
/// executable), with this test assembly as the worker plugin.
/// </summary>
public sealed class WorkerHostLogFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omp-wph-log-" + Guid.NewGuid().ToString("N"));

    private string AppDirectory => Path.Combine(_root, "app");

    // EnsureDefaultLogDirectory: a sibling of the host's own folder.
    private string LogDirectory => Path.Combine(_root, "Logs", "WorkerProcessHost");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task TwoConcurrentHostProcessesBothWriteTheirLinesToDisk()
    {
        CopyHostTo(AppDirectory);
        var processes = new[] { StartProbe("probe-a"), StartProbe("probe-b") };
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !(LogContains("key=probe-a") && LogContains("key=probe-b")))
            {
                await Task.Delay(200);
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                Stop(process);
            }
        }

        Assert.True(LogContains("key=probe-a"), "process probe-a wrote no line to disk: " + DescribeLogs());
        Assert.True(LogContains("key=probe-b"), "process probe-b wrote no line to disk: " + DescribeLogs());
        // One file per process, each starting with the host line that names its worker.
        var files = Directory.GetFiles(LogDirectory, "OpenModulePlatform.WorkerProcessHost-*.log");
        Assert.Equal(2, files.Length);
        Assert.Contains(files, file => File.ReadLines(file).First().Contains("WorkerInstanceKey=probe-a"));
        Assert.Contains(files, file => File.ReadLines(file).First().Contains("WorkerInstanceKey=probe-b"));
    }

    [Fact]
    public void AHostStartDeletesExpiredLogFilesOfExitedProcessesButNothingElse()
    {
        CopyHostTo(AppDirectory);
        Directory.CreateDirectory(LogDirectory);
        var expiredOtherProcess = Seed($"OpenModulePlatform.WorkerProcessHost-2026-09-01-{ExitedProcessId()}.log", days: 30);
        // A host that is still running but has written nothing for the whole
        // retention: NLog shares the file for delete, so only the process check
        // keeps it (this test process stands in for that live host).
        var expiredLiveProcess = Seed($"OpenModulePlatform.WorkerProcessHost-2026-09-01-{Environment.ProcessId}.log", days: 30);
        var expiredOldSingleFileName = Seed("OpenModulePlatform.WorkerProcessHost-2026-09-02.log", days: 30);
        var recent = Seed("OpenModulePlatform.WorkerProcessHost-2026-10-10-2222.log", days: 3);
        var unrelated = Seed("SomethingElse-2026-09-01.log", days: 30);

        // No worker settings: the host logs its start line and stops.
        using (var process = Process.Start(new ProcessStartInfo(HostExecutable) { WorkingDirectory = AppDirectory, UseShellExecute = false, CreateNoWindow = true })!)
        {
            Assert.True(process.WaitForExit(60_000), "the host did not stop");
        }

        Assert.False(File.Exists(expiredOtherProcess), "an expired file of another process must be deleted");
        Assert.True(File.Exists(expiredLiveProcess), "the file of a process that is still running must be kept whatever its age");
        Assert.False(File.Exists(expiredOldSingleFileName), "an expired file with the old single-file name must be deleted");
        Assert.True(File.Exists(recent), "a file inside the retention must be kept");
        Assert.True(File.Exists(unrelated), "a file that is not this host's log must never be touched");
    }

    [Fact]
    public void DeleteExpiredFilesUsesTheRetentionBoundary()
    {
        Directory.CreateDirectory(LogDirectory);
        var now = DateTime.UtcNow;
        var justInside = Seed("OpenModulePlatform.WorkerProcessHost-a-1.log", days: 0);
        File.SetLastWriteTimeUtc(justInside, now - TimeSpan.FromDays(WorkerHostLogFile.RetentionDays) + TimeSpan.FromMinutes(1));
        var justOutside = Seed("OpenModulePlatform.WorkerProcessHost-b-2.log", days: 0);
        File.SetLastWriteTimeUtc(justOutside, now - TimeSpan.FromDays(WorkerHostLogFile.RetentionDays) - TimeSpan.FromMinutes(1));

        Assert.Equal(1, WorkerHostLogFile.DeleteExpiredFiles(LogDirectory, now, TimeSpan.FromDays(WorkerHostLogFile.RetentionDays)));
        Assert.True(File.Exists(justInside));
        Assert.False(File.Exists(justOutside));
        Assert.Equal(0, WorkerHostLogFile.DeleteExpiredFiles(Path.Combine(_root, "missing"), now, TimeSpan.FromDays(14)));
    }

    [Fact]
    public void TheFileNameCarriesTheProcessInBothConfigurationPaths()
    {
        Assert.Contains("${processid}", WorkerHostLogFile.DefaultFileName);
        Assert.Equal(14, WorkerHostLogFile.RetentionDays);

        // The appsettings.json NLog section is not what a running host uses (the
        // code configuration is set first), but it is kept in step.
        using var appsettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var logfile = appsettings.RootElement.GetProperty("NLog").GetProperty("targets").GetProperty("logfile");
        Assert.Contains("${processid}", logfile.GetProperty("fileName").GetString());
        Assert.Equal(WorkerHostLogFile.RetentionDays, logfile.GetProperty("maxArchiveDays").GetInt32());
    }

    [Theory]
    [InlineData(new[] { "--WorkerProcess:WorkerInstanceKey=CHA_NU" }, "CHA_NU")]
    [InlineData(new[] { "--x=1", "--WorkerProcess:WorkerInstanceKey", "sim-webclient" }, "sim-webclient")]
    [InlineData(new[] { "--workerprocess:workerinstancekey=lower" }, "lower")]
    [InlineData(new string[0], "")]
    public void TheWorkerInstanceKeyIsReadFromTheCommandLine(string[] args, string expected)
        => Assert.Equal(expected, WorkerHostLogFile.WorkerInstanceKeyFromArgs(args));

    [Fact]
    public void DeleteExpiredFilesKeepsTheFileOfARunningProcessOnly()
    {
        var exited = Seed($"OpenModulePlatform.WorkerProcessHost-2026-09-01-{ExitedProcessId()}.log", days: 30);
        var running = Seed($"OpenModulePlatform.WorkerProcessHost-2026-09-01-{Environment.ProcessId}.log", days: 30);
        // A trailing number that is not a process (the old single-file name ends
        // in the day) gives no protection.
        var oldName = Seed($"OpenModulePlatform.WorkerProcessHost-2026-09-{Environment.ProcessId % 28 + 1:00}.log", days: 30);

        Assert.Equal(2, WorkerHostLogFile.DeleteExpiredFiles(LogDirectory, DateTime.UtcNow, TimeSpan.FromDays(WorkerHostLogFile.RetentionDays)));
        Assert.False(File.Exists(exited));
        Assert.True(File.Exists(running));
        Assert.False(File.Exists(oldName));
    }

    // The id of a process that has exited (reuse within the test is unlikely, and
    // would only make a deletion assertion fail, never pass wrongly).
    private static int ExitedProcessId()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        return process.Id;
    }

    private string HostExecutable => Path.Combine(AppDirectory, "OpenModulePlatform.WorkerProcessHost.exe");

    private static void CopyHostTo(string target)
    {
        var source = AppContext.BaseDirectory;
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("Logs", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(target, relative), overwrite: true);
        }
    }

    private Process StartProbe(string key)
    {
        var workerInstanceId = Guid.NewGuid();
        var info = new ProcessStartInfo(HostExecutable)
        {
            WorkingDirectory = AppDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     $"--WorkerProcess:AppInstanceId={Guid.NewGuid()}",
                     $"--WorkerProcess:WorkerInstanceId={workerInstanceId}",
                     $"--WorkerProcess:WorkerInstanceKey={key}",
                     $"--WorkerProcess:WorkerTypeKey={LoggingProbeWorkerFactory.TypeKey}",
                     $"--WorkerProcess:PluginAssemblyPath={Path.Combine(AppDirectory, Path.GetFileName(typeof(LoggingProbeWorkerFactory).Assembly.Location))}",
                     $"--WorkerProcess:ShutdownEventName=OpenModulePlatform.WorkerShutdown.{workerInstanceId:N}"
                 })
        {
            info.ArgumentList.Add(argument);
        }

        var process = Process.Start(info)!;
        // Drain the console so a full pipe can never block the host.
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private bool LogContains(string text)
    {
        if (!Directory.Exists(LogDirectory)) return false;
        foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log"))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if (reader.ReadToEnd().Contains(text, StringComparison.Ordinal)) return true;
            }
            catch (IOException)
            {
            }
        }

        return false;
    }

    private string DescribeLogs()
        => Directory.Exists(LogDirectory)
            ? string.Join(", ", Directory.EnumerateFiles(LogDirectory).Select(Path.GetFileName))
            : "(no log directory)";

    private string Seed(string name, int days)
    {
        Directory.CreateDirectory(LogDirectory);
        var path = Path.Combine(LogDirectory, name);
        File.WriteAllText(path, "seed\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));
        return path;
    }
}

/// <summary>
/// A worker plugin that lives in this test assembly: it logs a probe line every
/// 100 ms until the host stops it, so a host process stays alive and writes.
/// </summary>
public sealed class LoggingProbeWorkerFactory : IWorkerModuleFactory
{
    public const string TypeKey = "logging-probe-worker";

    public string WorkerTypeKey => TypeKey;

    public IWorkerModule Create(IServiceProvider services)
        => new LoggingProbeWorker(services.GetRequiredService<ILoggerFactory>().CreateLogger<LoggingProbeWorker>());

    private sealed class LoggingProbeWorker(ILogger<LoggingProbeWorker> logger) : IWorkerModule
    {
        public async Task RunAsync(WorkerExecutionContext context, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("LOGGING-PROBE alive key={WorkerInstanceKey}", context.WorkerInstanceKey);
                await Task.Delay(100, cancellationToken);
            }
        }
    }
}
