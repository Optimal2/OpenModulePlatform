// File: OpenModulePlatform.Service.ExampleServiceAppModule/Services/ExampleServiceAppModuleConfigService.cs
using OpenModulePlatform.Service.ExampleServiceAppModule.Models;
using System.Text.Json;

namespace OpenModulePlatform.Service.ExampleServiceAppModule.Services;

public sealed class ExampleServiceAppModuleConfigService
{
    private readonly AppInstanceRepository _appInstances;
    private readonly ExampleServiceAppModuleConfigurationRepository _configs;

    public ExampleServiceAppModuleConfigService(
        AppInstanceRepository appInstances,
        ExampleServiceAppModuleConfigurationRepository configs)
    {
        _appInstances = appInstances;
        _configs = configs;
    }

    public async Task<RefreshResult> RefreshAsync(Guid appInstanceId, CancellationToken ct)
    {
        var runtime = await _appInstances.GetRuntimeAsync(appInstanceId, ct);
        if (runtime is null)
            return new RefreshResult(null, null, "app_instance_not_found");

        if (!runtime.IsAllowed)
            return new RefreshResult(runtime, null, "app_instance_not_allowed");

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
        AppInstanceRepository.AppInstanceRuntime? Runtime,
        ExampleServiceAppModuleOptions? Config,
        string? StateReason);
}
