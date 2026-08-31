using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Runtime;
using OpenModulePlatform.WorkerManager.WindowsService.Services;
using OpenModulePlatform.WorkerProcessHost.Services;
using OpenModulePlatform.WorkerProcessHost.TestPlugin;

namespace OpenModulePlatform.WorkerManager.WindowsService.Tests;

public sealed class WorkerProcessCompatibilityArgumentsTests
{
    [Fact]
    public void CreateWorkerProcess_PassesResolvedHostAndPluginArtifactIdentity()
    {
        var desired = new DesiredWorkerInstance
        {
            AppInstanceId = Guid.NewGuid(),
            WorkerInstanceId = Guid.NewGuid(),
            WorkerInstanceKey = "worker-1",
            WorkerTypeKey = "worker-type",
            InstallRootPath = @"D:\Artifacts\plugin\0.3.73",
            PluginAssemblyPath = @"D:\Artifacts\plugin\0.3.73\Plugin.dll",
            ShutdownEventName = "shutdown"
        };
        var host = new ResolvedWorkerProcessHost(
            @"D:\Artifacts\omp-workerprocesshost\0.3.46\OpenModulePlatform.WorkerProcessHost.exe",
            ArtifactId: 46,
            Version: "0.3.46");

        using var process = WorkerManagerHostedService.CreateWorkerProcess(
            host.Path,
            desired,
            host,
            "Server=localhost;Database=OpenModulePlatform;Integrated Security=True;");

        Assert.Contains(
            "--WorkerProcess:PluginArtifactRootPath=D:\\Artifacts\\plugin\\0.3.73",
            process.StartInfo.ArgumentList);
        Assert.Contains(
            "--WorkerProcess:WorkerHostComponentKey=omp-workerprocesshost",
            process.StartInfo.ArgumentList);
        Assert.Contains(
            "--WorkerProcess:WorkerHostArtifactVersion=0.3.46",
            process.StartInfo.ArgumentList);
        Assert.True(process.StartInfo.RedirectStandardError);
    }

    [Fact]
    public async Task StartWorkerProcess_ForwardsActualStandardErrorBeforeExitIsReported()
    {
        var standardError = new List<string>();
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("/d");
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("echo worker-host compatibility rejection 1>&2 & exit /b 1");

        WorkerManagerHostedService.StartWorkerProcess(
            process,
            Guid.NewGuid(),
            process.StartInfo.FileName,
            line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    standardError.Add(line);
                }
            });
        await process.WaitForExitAsync();
        process.WaitForExit();

        Assert.Equal(1, process.ExitCode);
        Assert.Contains(standardError, line => line.Contains("worker-host compatibility rejection", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RealWorkerProcessHost_PropagatesLoaderCompatibilityRejectionToStandardError()
    {
        var artifactRoot = Path.Join(
            Path.GetTempPath(),
            "OpenModulePlatform",
            "WorkerManagerCompatibilityProcessTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(artifactRoot, "omp-worker-plugin.json"),
                """
                {
                  "formatVersion": 1,
                  "workerHost": {
                    "componentKey": "omp-workerprocesshost",
                    "minVersion": "0.3.46"
                  }
                }
                """);

            var standardError = new List<string>();
            var standardErrorClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add(typeof(WorkerProcessHostedService).Assembly.Location);
            process.StartInfo.ArgumentList.Add($"--WorkerProcess:AppInstanceId={Guid.NewGuid():D}");
            process.StartInfo.ArgumentList.Add("--WorkerProcess:WorkerInstanceKey=compatibility-proof");
            process.StartInfo.ArgumentList.Add("--WorkerProcess:WorkerTypeKey=newer-contract-test-worker");
            process.StartInfo.ArgumentList.Add($"--WorkerProcess:PluginAssemblyPath={typeof(CompatibilityTestWorkerFactory).Assembly.Location}");
            process.StartInfo.ArgumentList.Add($"--WorkerProcess:PluginArtifactRootPath={artifactRoot}");
            process.StartInfo.ArgumentList.Add("--WorkerProcess:WorkerHostComponentKey=omp-workerprocesshost");
            process.StartInfo.ArgumentList.Add("--WorkerProcess:WorkerHostArtifactVersion=0.3.45");

            WorkerManagerHostedService.StartWorkerProcess(
                process,
                Guid.NewGuid(),
                process.StartInfo.FileName,
                line =>
                {
                    if (line is null)
                    {
                        standardErrorClosed.TrySetResult(true);
                        return;
                    }

                    lock (standardError)
                    {
                        standardError.Add(line);
                    }
                });
            await process.WaitForExitAsync();
            await standardErrorClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, process.ExitCode);
            lock (standardError)
            {
                Assert.Contains(
                    standardError,
                    line => line.Contains("requires component 'omp-workerprocesshost' version 0.3.46 or later", StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [Fact]
    public void ObserveExitIfNeeded_BoundsMissingStandardErrorEofWait()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /c exit /b 1",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        process.WaitForExit();

        var desired = new DesiredWorkerInstance
        {
            AppInstanceId = Guid.NewGuid(),
            WorkerInstanceId = Guid.NewGuid(),
            WorkerInstanceKey = "bounded-stderr-proof",
            WorkerTypeKey = "compatibility-proof",
            PluginAssemblyPath = @"C:\does-not-matter\Plugin.dll",
            ShutdownEventName = "unused"
        };
        var managed = new ManagedWorkerProcess(desired);
        managed.ResetStandardError();
        managed.AttachProcess(
            process,
            new EventWaitHandle(false, EventResetMode.ManualReset),
            DateTimeOffset.UtcNow);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(managed.ObserveExitIfNeeded());
        stopwatch.Stop();

        Assert.True(managed.StandardErrorDrainTimedOut);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    }
}
