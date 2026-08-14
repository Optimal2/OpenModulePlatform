namespace OpenModulePlatform.HostAgent.Runtime.Models;

/// <summary>
/// Where one app instance currently lives on this host: its Windows service or IIS
/// application name, and the directory its files were deployed to.
/// </summary>
/// <remarks>
/// Read for every app instance on the host, web and service alike, and deliberately not
/// limited by the deployment cycle's artifact cap. Rename cleanup deletes a directory
/// recursively, and the only safe basis for that is knowing what every other instance
/// occupies -- not just the ones that happened to fit in this cycle (R7-D4).
/// </remarks>
public sealed record HostRuntimeFootprint(
    Guid AppInstanceId,
    string? RuntimeName,
    string? TargetPath);
