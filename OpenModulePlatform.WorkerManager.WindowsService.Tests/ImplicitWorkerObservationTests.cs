using System.Reflection;
using OpenModulePlatform.WorkerManager.WindowsService.Models;
using OpenModulePlatform.WorkerManager.WindowsService.Runtime;
using OpenModulePlatform.WorkerManager.WindowsService.Services;

namespace OpenModulePlatform.WorkerManager.WindowsService.Tests;

public sealed class ImplicitWorkerObservationTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    public void ImplicitWorker_RuntimeSummaryIdentifiesFallbackAndPreservesDiagnostic(byte state)
    {
        var id = Guid.NewGuid();
        var managed = new ManagedWorkerProcess(new DesiredWorkerInstance
        {
            AppInstanceId = id, WorkerInstanceId = id, IsImplicitDefault = true
        });
        var method = typeof(WorkerManagerHostedService).GetMethod("CreateObservation", BindingFlags.NonPublic | BindingFlags.Static)!;
        var observation = (WorkerRuntimeObservation)method.Invoke(null,
            [managed, "windows-worker-plugin", state, null, null, null, "process diagnostic"] )!;

        Assert.Contains("Implicit default worker", observation.StatusMessage);
        Assert.Contains("process diagnostic", observation.StatusMessage);
        Assert.Equal(state, observation.ObservedState);
    }

    [Fact]
    public void ExplicitWorker_WithAppInstanceId_DoesNotGetImplicitWarning()
    {
        var id = Guid.NewGuid();
        var managed = new ManagedWorkerProcess(new DesiredWorkerInstance { AppInstanceId = id, WorkerInstanceId = id });
        var method = typeof(WorkerManagerHostedService).GetMethod("CreateObservation", BindingFlags.NonPublic | BindingFlags.Static)!;
        var observation = (WorkerRuntimeObservation)method.Invoke(null,
            [managed, "windows-worker-plugin", (byte)2, null, null, null, "process diagnostic"])!;

        Assert.Equal("process diagnostic", observation.StatusMessage);
    }

    [Fact]
    public void ImplicitIdentity_SurvivesCacheResolutionAndChangesReconciliationIdentity()
    {
        var id = Guid.NewGuid();
        var implicitWorker = new DesiredWorkerInstance
        {
            AppInstanceId = id, WorkerInstanceId = id, IsImplicitDefault = true, PluginRelativePath = "Worker.dll"
        };
        var explicitWorker = new DesiredWorkerInstance
        {
            AppInstanceId = id, WorkerInstanceId = id, PluginRelativePath = "Worker.dll"
        };

        Assert.True(implicitWorker.WithInstallRootPath(@"C:\workers\cache").IsImplicitDefault);
        Assert.False(implicitWorker.HasEquivalentConfiguration(explicitWorker));
    }
}
