using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Services;

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
    }
}
