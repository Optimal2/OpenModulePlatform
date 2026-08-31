using System.Text.Json.Serialization;

namespace OpenModulePlatform.Worker.Abstractions.Models;

/// <summary>
/// Portable compatibility metadata generated into a worker plugin artifact.
/// </summary>
public sealed class WorkerPluginCompatibilityManifest
{
    public const string FileName = "omp-worker-plugin.json";

    public const string DefaultWorkerHostComponentKey = "omp-workerprocesshost";

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    [JsonPropertyName("workerHost")]
    public WorkerHostCompatibilityRequirement? WorkerHost { get; init; }
}

/// <summary>
/// Identifies the minimum worker-host artifact version required by a plugin.
/// </summary>
public sealed class WorkerHostCompatibilityRequirement
{
    [JsonPropertyName("componentKey")]
    public string ComponentKey { get; init; } = string.Empty;

    [JsonPropertyName("minVersion")]
    public string MinVersion { get; init; } = string.Empty;
}
