namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Thin abstraction over Windows service-control operations so that
/// <see cref="ServiceAppDeploymentService"/> can be unit-tested without
/// calling sc.exe or WMI.
/// </summary>
public interface IWindowsServiceControl
{
    string? GetServiceState(string serviceName);

    bool IsServiceRunning(string serviceName);

    void StartServiceIfStopped(string serviceName, int timeoutSeconds);

    bool StopServiceIfRunning(string serviceName, int timeoutSeconds);

    void EnsureServiceConfigured(
        string serviceName,
        string executablePath,
        string displayName,
        string description);
}
