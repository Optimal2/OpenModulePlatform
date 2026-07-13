using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

public sealed class DeploySetConsistencyService
{
    private readonly IOptionsMonitor<HostAgentSettings> _settings;
    private readonly IOmpHostArtifactRepository _repository;
    private readonly ILogger<DeploySetConsistencyService> _logger;

    public DeploySetConsistencyService(
        IOptionsMonitor<HostAgentSettings> settings,
        IOmpHostArtifactRepository repository,
        ILogger<DeploySetConsistencyService> logger)
    {
        _settings = settings;
        _repository = repository;
        _logger = logger;
    }

    public async Task<DeploySetConsistencyCheckSummary> CheckAsync(
        string hostKey,
        IReadOnlyList<ArtifactDescriptor> artifacts,
        CancellationToken ct)
    {
        var mode = DeploySetConsistencyModes.Normalize(_settings.CurrentValue.DeploySetConsistencyMode);
        if (string.Equals(mode, DeploySetConsistencyModes.None, StringComparison.OrdinalIgnoreCase))
        {
            return DeploySetConsistencyCheckSummary.Empty;
        }

        var artifactIds = artifacts.Select(a => a.ArtifactId).Distinct().ToList();
        var results = await _repository.GetDeploySetConsistencyResultsAsync(hostKey, artifactIds, ct);
        var deviations = results.Where(r => !r.IsConsistent).ToList();

        foreach (var deviation in deviations)
        {
            var message =
                $"Deploy-set consistency deviation detected. " +
                $"ModuleInstance={deviation.ModuleInstanceKey} ({deviation.ModuleKey}), " +
                $"Set={deviation.SetKey}, " +
                $"MatchedMembers={deviation.MatchedMemberCount}/{deviation.TotalMemberCount}, " +
                $"Versions={deviation.ActualVersions}. " +
                $"All artifacts in the set should use the same version.";

            _logger.LogWarning(message);
        }

        return new DeploySetConsistencyCheckSummary(results, deviations);
    }

    public void ThrowIfBlocked(DeploySetConsistencyCheckSummary summary)
    {
        var mode = DeploySetConsistencyModes.Normalize(_settings.CurrentValue.DeploySetConsistencyMode);
        if (!string.Equals(mode, DeploySetConsistencyModes.Block, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (summary.DeviationCount == 0)
        {
            return;
        }

        var first = summary.Deviations[0];
        throw new InvalidOperationException(
            $"Deploy-set consistency check is configured to Block and detected a version mismatch. " +
            $"ModuleInstance={first.ModuleInstanceKey} ({first.ModuleKey}), Set={first.SetKey}, " +
            $"Versions={first.ActualVersions}. " +
            $"Rebuild and import a consistent artifact set, or switch DeploySetConsistencyMode to Warn.");
    }
}

public sealed record DeploySetConsistencyCheckSummary(
    IReadOnlyList<DeploySetConsistencyCheckResult> Results,
    IReadOnlyList<DeploySetConsistencyCheckResult> Deviations)
{
    public int DeviationCount => Deviations.Count;

    public bool HasDeviations => Deviations.Count > 0;

    public static DeploySetConsistencyCheckSummary Empty { get; } = new(
        Array.Empty<DeploySetConsistencyCheckResult>(),
        Array.Empty<DeploySetConsistencyCheckResult>());
}
