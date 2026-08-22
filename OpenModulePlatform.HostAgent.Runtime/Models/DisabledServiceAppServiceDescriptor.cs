namespace OpenModulePlatform.HostAgent.Runtime.Models;

/// <summary>
/// A service-app app instance targeted at this host that is switched off
/// (<c>IsEnabled = 0</c> or <c>DesiredState = 0</c>) but still has a recorded runtime
/// deployment here. <see cref="RuntimeName"/> and <see cref="TargetPath"/> come from
/// omp.HostAppDeploymentStates -- the platform's own record, written when HostAgent
/// itself deployed the instance -- and are the attribution basis for removing the
/// now-unmanaged Windows service.
/// </summary>
public sealed class DisabledServiceAppServiceDescriptor
{
    public Guid AppInstanceId { get; init; }

    public string AppInstanceKey { get; init; } = string.Empty;

    public bool IsEnabled { get; init; }

    public byte DesiredState { get; init; }

    public string? InstallPath { get; init; }

    public string? InstallationName { get; init; }

    public string RuntimeName { get; init; } = string.Empty;

    public string TargetPath { get; init; } = string.Empty;
}
