using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class MaintenanceScanSchedulerTests
{
    [Fact]
    public async Task ExecuteAsync_WhenDisabled_DoesNotEnqueue()
    {
        var repository = new FakeOmpHostArtifactRepository();
        var settings = new FakeOptionsMonitor<HostAgentSettings>
        {
            CurrentValue = new HostAgentSettings
            {
                MaintenanceScanIntervalMinutes = 0
            }
        };
        var process = new HostAgentProcessContext("OMP.HostAgent", "1.0.0", HostAgentRuntimeMode.Normal, null);
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var scheduler = new MaintenanceScanScheduler(
            repository,
            settings,
            process,
            timeProvider,
            NullLogger<MaintenanceScanScheduler>.Instance);

        await scheduler.StartAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Empty(repository.EnqueuedMaintenanceScanHostKeys);
    }

    [Fact]
    public async Task ExecuteAsync_AfterInterval_EnqueuesMaintenanceScanForHostKey()
    {
        var repository = new FakeOmpHostArtifactRepository();
        var settings = new FakeOptionsMonitor<HostAgentSettings>
        {
            CurrentValue = new HostAgentSettings
            {
                HostKey = "test-host",
                MaintenanceScanIntervalMinutes = 5
            }
        };
        var process = new HostAgentProcessContext("OMP.HostAgent", "1.0.0", HostAgentRuntimeMode.Normal, null);
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var scheduler = new MaintenanceScanScheduler(
            repository,
            settings,
            process,
            timeProvider,
            NullLogger<MaintenanceScanScheduler>.Instance);

        await scheduler.StartAsync(CancellationToken.None);

        // Wait for the scheduler to register its delay timer, then advance
        // past the first interval to trigger the enqueue.
        await WaitUntilAsync(() => timeProvider.HasPendingTimer);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await WaitUntilAsync(() => repository.EnqueuedMaintenanceScanHostKeys.Count == 1);

        Assert.Single(repository.EnqueuedMaintenanceScanHostKeys, "test-host");

        await scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_AfterMultipleIntervals_EnqueuesRepeatedly()
    {
        var repository = new FakeOmpHostArtifactRepository();
        var settings = new FakeOptionsMonitor<HostAgentSettings>
        {
            CurrentValue = new HostAgentSettings
            {
                HostKey = "test-host",
                MaintenanceScanIntervalMinutes = 5
            }
        };
        var process = new HostAgentProcessContext("OMP.HostAgent", "1.0.0", HostAgentRuntimeMode.Normal, null);
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var scheduler = new MaintenanceScanScheduler(
            repository,
            settings,
            process,
            timeProvider,
            NullLogger<MaintenanceScanScheduler>.Instance);

        await scheduler.StartAsync(CancellationToken.None);

        // Each advance must wait until the scheduler has registered its next
        // delay timer; advancing earlier strands the following wait a full
        // interval past the manual clock and the second enqueue never fires.
        await WaitUntilAsync(() => timeProvider.HasPendingTimer);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await WaitUntilAsync(() => repository.EnqueuedMaintenanceScanHostKeys.Count == 1);

        await WaitUntilAsync(() => timeProvider.HasPendingTimer);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await WaitUntilAsync(() => repository.EnqueuedMaintenanceScanHostKeys.Count == 2);

        Assert.Equal(2, repository.EnqueuedMaintenanceScanHostKeys.Count);
        Assert.All(repository.EnqueuedMaintenanceScanHostKeys, hostKey => Assert.Equal("test-host", hostKey));

        await scheduler.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Timed out waiting for the scheduler to reach the expected state.");
    }
}
