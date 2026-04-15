// File: OpenModulePlatform.Worker.ExampleWorkerAppModule/Services/ExampleWorkerAppModuleConfigService.cs
using System.Text.Json;
using OpenModulePlatform.Worker.ExampleWorkerAppModule.Models;

namespace OpenModulePlatform.Worker.ExampleWorkerAppModule.Services;

public sealed class ExampleWorkerAppModuleConfigService
{
    private readonly AppInstanceRepository _appInstances;
    private readonly ExampleWorkerAppModuleConfigurationRepository _configs;

    public ExampleWorkerAppModuleConfigService(
        AppInstanceRepository appInstances,
        ExampleWorkerAppModuleConfigurationRepository configs)
    {
        _appInstances = appInstances;
        _configs = configs;
    }

    public async Task<RefreshResult> RefreshAsync(Guid appInstanceId, CancellationToken ct)
    {
        var runtime = await _appInstances.GetRuntimeAsync(appInstanceId, ct);
        if (runtime is null)
        {
            return new RefreshResult(null, null, "app_instance_not_found");
        }

        if (!runtime.IsAllowed)
        {
            return new RefreshResult(runtime, null, "app_instance_not_allowed");
        }

        if (runtime.DesiredState == 0)
        {
            return new RefreshResult(runtime, null, "desired_state_disabled");
        }

        if (!runtime.ConfigId.HasValue)
        {
            return new RefreshResult(runtime, new ExampleWorkerAppModuleOptions(), "no_config_assigned");
        }

        var json = await _configs.GetConfigurationJsonAsync(runtime.ConfigId.Value, ct);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RefreshResult(runtime, null, "config_not_found");
        }

        var config = JsonSerializer.Deserialize<ExampleWorkerAppModuleOptions>(
                         json,
                         new JsonSerializerOptions(JsonSerializerDefaults.Web))
                     ?? new ExampleWorkerAppModuleOptions();

        return new RefreshResult(runtime, config, null);
    }

    public sealed record RefreshResult(
        AppInstanceRepository.AppInstanceRuntime? Runtime,
        ExampleWorkerAppModuleOptions? Config,
        string? StateReason);
}
