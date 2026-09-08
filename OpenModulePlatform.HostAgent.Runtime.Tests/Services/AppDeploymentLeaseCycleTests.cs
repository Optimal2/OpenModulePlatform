using Microsoft.Extensions.Logging;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class AppDeploymentLeaseCycleTests
{
    private readonly FakeOmpHostArtifactRepository _repository = new() { EnabledHostCount = 2 };
    private readonly CaptureLogger _logger = new();
    private readonly Guid _first = Guid.NewGuid();
    private readonly Guid _second = Guid.NewGuid();

    private Task<AppDeploymentLeaseCycle> Cycle(string scope = "app") =>
        AppDeploymentLeaseCycle.CreateAsync(_repository,
            new HostAgentSettings { DeploymentLockScope = scope }, _logger, CancellationToken.None);

    [Fact]
    public async Task App_SameAppWaitsWithoutExecutingAndRetriesNextCycle()
    {
        await using var first = await Cycle();
        await using var second = await Cycle();
        var executions = 0;
        if (await first.EnterAsync(_first, "portal", default)) executions++;
        if (await second.EnterAsync(_second, "portal", default)) executions++;
        Assert.Equal(1, executions);
        Assert.Contains("app:portal", second.WaitingMessage);
        Assert.Contains(_first.ToString(), second.WaitingMessage);
        Assert.Contains("UTC", second.WaitingMessage);
        Assert.Contains(_logger.Messages, x => x.Contains("Waiting for lease"));
        await first.CompleteAppAsync();
        await using var retry = await Cycle();
        Assert.True(await retry.EnterAsync(_second, "portal", default));
    }

    [Fact]
    public async Task App_DifferentAppsCanDeployConcurrently()
    {
        await using var first = await Cycle();
        await using var second = await Cycle();
        Assert.True(await first.EnterAsync(_first, "portal", default));
        Assert.True(await second.EnterAsync(_second, "auth", default));
        Assert.Equal(2, _repository.AppLeases.Count);
    }

    [Fact]
    public async Task Host_HoldsOneLeaseAcrossAppsUntilCombinedSweepEnds()
    {
        await using var first = await Cycle("host");
        await using var second = await Cycle("host");
        Assert.True(await first.EnterAsync(_first, "portal", default));
        await first.CompleteAppAsync();
        Assert.False(await second.EnterAsync(_second, "auth", default));
        Assert.True(await first.EnterAsync(_first, "auth", default));
        await first.CompleteAppAsync();
        Assert.Equal(2, _repository.AppLeaseCalls); // one per host, not per app
        Assert.Empty(_repository.ReleasedAppLeases);
        await first.DisposeAsync();
        Assert.Equal("host:*", Assert.Single(_repository.ReleasedAppLeases).ScopeKey);
    }

    [Fact]
    public async Task ExpiredLeaseCanBeTakenOverAndStaleReleaseCannotRemoveSuccessor()
    {
        await using var first = await Cycle();
        Assert.True(await first.EnterAsync(_first, "portal", default));
        _repository.LeaseNowUtc = _repository.LeaseNowUtc.AddSeconds(601);
        await using var second = await Cycle();
        Assert.True(await second.EnterAsync(_second, "portal", default));
        await first.CompleteAppAsync();
        Assert.Equal(_second, _repository.AppLeases["app:portal"].HostId);
        Assert.Contains(_logger.Messages, x => x.Contains("expired lease takeover=True", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("app")]
    [InlineData("host")]
    public async Task FailureReleasesWithReasonAndHostModeStopsRemainingChanges(string scope)
    {
        await using var cycle = await Cycle(scope);
        Assert.True(await cycle.EnterAsync(_first, "portal", default));
        await cycle.CompleteAppAsync("Readiness failed");
        Assert.Equal("Readiness failed", Assert.Single(_repository.ReleasedAppLeases).Reason);
        Assert.Empty(_repository.AppLeases);
        if (scope == "host") Assert.False(await cycle.EnterAsync(_first, "auth", default));
    }

    [Theory]
    [InlineData(1, "app")]
    [InlineData(1, "host")]
    [InlineData(0, "app")]
    [InlineData(2, "off")]
    public async Task SingleHostAndOffUseOriginalPathWithoutAnyLeaseCalls(int hosts, string scope)
    {
        _repository.EnabledHostCount = hosts;
        await using var cycle = await Cycle(scope);
        Assert.True(await cycle.EnterAsync(_first, "portal", default));
        await cycle.CompleteAppAsync();
        Assert.False(cycle.IsCoordinated);
        Assert.Equal(0, _repository.AppLeaseCalls);
        Assert.Empty(_repository.ReleasedAppLeases);
        Assert.Equal(0, _repository.HostAgentLeaseCalls); // the per-host agent lease is a different mechanism
    }

    [Fact]
    public async Task HostCountUnavailableKeepsConfiguredCoordinationAndLogs()
    {
        _repository.EnabledHostCountException = new InvalidOperationException("Database unavailable");
        await using var cycle = await Cycle("host");
        Assert.True(cycle.IsCoordinated);
        Assert.True(await cycle.EnterAsync(_first, "portal", default));
        Assert.Contains("host:*", _repository.AppLeases.Keys);
        Assert.Contains(_logger.Messages, x => x.Contains("host count", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, _repository.HostAgentLeaseCalls);
    }

    [Fact]
    public async Task App_SkipReleasesLeaseWithSkipReasonAndKeepsCycleUsable()
    {
        await using var cycle = await Cycle();
        Assert.True(await cycle.EnterAsync(_first, "portal", default));
        await cycle.SkipAppAsync("local deployment lock became active");
        var release = Assert.Single(_repository.ReleasedAppLeases);
        Assert.StartsWith("Skipped", release.Reason);
        Assert.DoesNotContain("interrupted", release.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(await cycle.EnterAsync(_first, "auth", default));
    }

    [Fact]
    public async Task Host_SkipKeepsSweepLeaseAndDoesNotStopRemainingChanges()
    {
        var cycle = await Cycle("host");
        Assert.True(await cycle.EnterAsync(_first, "portal", default));
        await cycle.SkipAppAsync("local deployment lock became active");
        Assert.Empty(_repository.ReleasedAppLeases);
        Assert.True(await cycle.EnterAsync(_first, "auth", default));
        await cycle.CompleteAppAsync();
        await cycle.DisposeAsync();
        Assert.Null(Assert.Single(_repository.ReleasedAppLeases).Reason);
    }

    [Fact]
    public async Task Host_EmptySweepDoesNotAcquire()
    {
        await using (await Cycle("host")) { }
        Assert.Equal(0, _repository.AppLeaseCalls);
    }

    [Theory]
    [InlineData("app", "host")]
    [InlineData("host", "app")]
    public async Task OverlappingModeSnapshotsStillCoordinate(string firstScope, string secondScope)
    {
        await using var first = await Cycle(firstScope);
        await using var second = await Cycle(secondScope);
        Assert.True(await first.EnterAsync(_first, "portal", default));
        Assert.False(await second.EnterAsync(_second, "auth", default));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("invalid", "zero")]
    public async Task MissingOrInvalidDatabaseSettingsUseLoggedLocalFallback(string? scope, string? seconds)
    {
        _repository.DeploymentSettings["DeploymentLockScope"] = scope;
        _repository.DeploymentSettings["DeploymentLeaseSeconds"] = seconds;
        await using var cycle = await Cycle("host");
        Assert.True(await cycle.EnterAsync(_first, "portal", default));
        Assert.Contains("host:*", _repository.AppLeases.Keys);
        Assert.Equal(_repository.LeaseNowUtc.AddSeconds(600), _repository.AppLeases["host:*"].LeaseUntilUtc);
        Assert.Contains(_logger.Messages, x => x.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DatabaseUnavailableUsesLocalFallbackAndLogs()
    {
        _repository.DeploymentSettingsException = new InvalidOperationException("Database unavailable");
        await using var cycle = await Cycle("host");
        Assert.True(await cycle.EnterAsync(_first, "portal", default));
        Assert.Contains("host:*", _repository.AppLeases.Keys);
        Assert.Contains(_logger.Messages, x => x.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DatabaseOverridesLocalSettingsOncePerCycle()
    {
        _repository.DeploymentSettings["DeploymentLockScope"] = "host";
        _repository.DeploymentSettings["DeploymentLeaseSeconds"] = "123";
        await using var cycle = await Cycle("off");
        Assert.True(await cycle.EnterAsync(_first, "portal", default));
        Assert.Equal(_repository.LeaseNowUtc.AddSeconds(123), _repository.AppLeases["host:*"].LeaseUntilUtc);
        Assert.Equal(1, _repository.DeploymentSettingsReads);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("86401")]
    public async Task InvalidDatabaseSecondsUseConfiguredLocalLifetime(string seconds)
    {
        _repository.DeploymentSettings["DeploymentLeaseSeconds"] = seconds;
        await using var cycle = await AppDeploymentLeaseCycle.CreateAsync(_repository,
            new HostAgentSettings { DeploymentLeaseSeconds = 90 }, _logger, default);
        Assert.True(await cycle.EnterAsync(_first, "portal", default));
        Assert.Equal(_repository.LeaseNowUtc.AddSeconds(90), _repository.AppLeases["app:portal"].LeaseUntilUtc);
    }

    [Fact]
    public async Task InterruptedSweepReleasesEvenWhenOperationTokenWasCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var cycle = await Cycle("host");
        Assert.True(await cycle.EnterAsync(_first, "portal", cancellation.Token));
        cancellation.Cancel();
        await cycle.DisposeAsync();
        Assert.Empty(_repository.AppLeases);
        Assert.Contains("interrupted", Assert.Single(_repository.ReleasedAppLeases).Reason);
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
