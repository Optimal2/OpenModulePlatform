using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class DeploySetConsistencyServiceTests
{
    private static DeploySetConsistencyService CreateService(
        string mode,
        IReadOnlyList<DeploySetConsistencyCheckResult> results)
    {
        var repository = new FakeOmpHostArtifactRepository
        {
            ConsistencyResults = results
        };
        var optionsMonitor = new FakeOptionsMonitor<HostAgentSettings>
        {
            CurrentValue = new HostAgentSettings
            {
                DeploySetConsistencyMode = mode
            }
        };

        return new DeploySetConsistencyService(
            optionsMonitor,
            repository,
            NullLogger<DeploySetConsistencyService>.Instance);
    }

    [Fact]
    public async Task CheckAsync_ModeNone_ReturnsEmptySummary()
    {
        var service = CreateService(DeploySetConsistencyModes.None,
        [
            new DeploySetConsistencyCheckResult(
                Guid.NewGuid(), "mi", "mod", "default",
                IsConsistent: false,
                ExpectedVersion: "1.0.0",
                ActualVersions: "a=1.0.0, b=2.0.0",
                MatchedMemberCount: 2,
                TotalMemberCount: 2)
        ]);

        var summary = await service.CheckAsync("host", [], CancellationToken.None);

        Assert.Empty(summary.Results);
        Assert.Empty(summary.Deviations);
    }

    [Fact]
    public async Task CheckAsync_ModeWarn_WithDeviation_ReturnsDeviation()
    {
        var deviation = new DeploySetConsistencyCheckResult(
            Guid.NewGuid(), "mi", "mod", "default",
            IsConsistent: false,
            ExpectedVersion: "1.0.0",
            ActualVersions: "a=1.0.0, b=2.0.0",
            MatchedMemberCount: 2,
            TotalMemberCount: 2);
        var service = CreateService(DeploySetConsistencyModes.Warn, [deviation]);

        var summary = await service.CheckAsync(
            "host",
            [new ArtifactDescriptor { ArtifactId = 1, Version = "1.0.0", PackageType = "web-app" }],
            CancellationToken.None);

        Assert.Single(summary.Results);
        Assert.Single(summary.Deviations);
        Assert.Same(deviation, summary.Deviations[0]);
    }

    [Fact]
    public async Task CheckAsync_ModeWarn_WithoutDeviation_ReturnsNoDeviation()
    {
        var consistent = new DeploySetConsistencyCheckResult(
            Guid.NewGuid(), "mi", "mod", "default",
            IsConsistent: true,
            ExpectedVersion: "1.0.0",
            ActualVersions: "a=1.0.0, b=1.0.0",
            MatchedMemberCount: 2,
            TotalMemberCount: 2);
        var service = CreateService(DeploySetConsistencyModes.Warn, [consistent]);

        var summary = await service.CheckAsync(
            "host",
            [new ArtifactDescriptor { ArtifactId = 1, Version = "1.0.0", PackageType = "web-app" }],
            CancellationToken.None);

        Assert.Single(summary.Results);
        Assert.Empty(summary.Deviations);
    }

    [Fact]
    public void ThrowIfBlocked_ModeBlock_WithDeviation_Throws()
    {
        var deviation = new DeploySetConsistencyCheckResult(
            Guid.NewGuid(), "mi", "mod", "default",
            IsConsistent: false,
            ExpectedVersion: null,
            ActualVersions: "a=1.0.0, b=2.0.0",
            MatchedMemberCount: 2,
            TotalMemberCount: 2);
        var service = CreateService(DeploySetConsistencyModes.Block, []);

        var summary = new DeploySetConsistencyCheckSummary(
            [deviation],
            [deviation]);

        var ex = Assert.Throws<InvalidOperationException>(() => service.ThrowIfBlocked(summary));
        Assert.Contains("Block", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mi", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThrowIfBlocked_ModeBlock_WithoutDeviation_DoesNotThrow()
    {
        var service = CreateService(DeploySetConsistencyModes.Block, []);
        var summary = DeploySetConsistencyCheckSummary.Empty;

        service.ThrowIfBlocked(summary);
    }

    [Fact]
    public void ThrowIfBlocked_ModeWarn_DoesNotThrow()
    {
        var deviation = new DeploySetConsistencyCheckResult(
            Guid.NewGuid(), "mi", "mod", "default",
            IsConsistent: false,
            ExpectedVersion: null,
            ActualVersions: "a=1.0.0, b=2.0.0",
            MatchedMemberCount: 2,
            TotalMemberCount: 2);
        var service = CreateService(DeploySetConsistencyModes.Warn, []);
        var summary = new DeploySetConsistencyCheckSummary([deviation], [deviation]);

        service.ThrowIfBlocked(summary);
    }
}
