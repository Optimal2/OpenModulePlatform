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
        var managed = new ManagedWorkerProcess(new DesiredWorkerInstance { AppInstanceId = id, WorkerInstanceId = id });
        var method = typeof(WorkerManagerHostedService).GetMethod("CreateObservation", BindingFlags.NonPublic | BindingFlags.Static)!;
        var observation = (WorkerRuntimeObservation)method.Invoke(null,
            [managed, "windows-worker-plugin", state, null, null, null, "process diagnostic"] )!;

        Assert.Contains("Implicit default worker", observation.StatusMessage);
        Assert.Contains("process diagnostic", observation.StatusMessage);
        Assert.Equal(state, observation.ObservedState);
    }
}
