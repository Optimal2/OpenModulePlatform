// File: OpenModulePlatform.Service.ExampleServiceAppModule/Services/ExampleServiceAppModuleConfigService.cs
using OpenModulePlatform.Service.ExampleServiceAppModule.Models;
using System.Text.Json;

namespace OpenModulePlatform.Service.ExampleServiceAppModule.Services;

public sealed class ExampleServiceAppModuleConfigService
{
    private readonly HostInstallationRepository _hostInstallations;
    private readonly ExampleServiceAppModuleConfigurationRepository _configs;

    public ExampleServiceAppModuleConfigService(
        HostInstallationRepository hostInstallations,
        ExampleServiceAppModuleConfigurationRepository configs)
    {
        _hostInstallations = hostInstallations;
        _configs = configs;
    }

    public async Task<RefreshResult> RefreshAsync(Guid hostInstallationId, CancellationToken ct)
    {
        var runtime = await _hostInstallations.GetRuntimeAsync(hostInstallationId, ct);
        if (runtime is null)
            return new RefreshResult(null, null, "installation_not_found");

        if (!runtime.IsAllowed)
            return new RefreshResult(runtime, null, "installation_not_allowed");

        if (runtime.DesiredState == 0)
            return new RefreshResult(runtime, null, "desired_state_disabled");

        if (!runtime.ConfigId.HasValue)
            return new RefreshResult(runtime, new ExampleServiceAppModuleOptions(), "no_config_assigned");

        var json = await _configs.GetConfigurationJsonAsync(runtime.ConfigId.Value, ct);
        if (string.IsNullOrWhiteSpace(json))
            return new RefreshResult(runtime, null, "config_not_found");

        var config = JsonSerializer.Deserialize<ExampleServiceAppModuleOptions>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? new ExampleServiceAppModuleOptions();

        return new RefreshResult(runtime, config, null);
    }

    public sealed record RefreshResult(
        HostInstallationRepository.HostInstallationRuntime? Runtime,
        ExampleServiceAppModuleOptions? Config,
        string? StateReason);
}
