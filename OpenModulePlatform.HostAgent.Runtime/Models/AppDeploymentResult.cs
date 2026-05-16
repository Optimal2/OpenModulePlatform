namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class AppDeploymentResult
{
    public byte State { get; init; }

    public bool Applied { get; init; }

    public string? TargetPath { get; init; }

    public string? RuntimeName { get; init; }

    public string? ErrorMessage { get; init; }

    public bool IsSuccess => State == HostDeploymentStatuses.Succeeded;

    public static AppDeploymentResult Succeeded(string targetPath, string? runtimeName, bool applied)
    {
        return new AppDeploymentResult
        {
            State = HostDeploymentStatuses.Succeeded,
            Applied = applied,
            TargetPath = targetPath,
            RuntimeName = runtimeName
        };
    }

    public static AppDeploymentResult Running(string targetPath, string? runtimeName)
    {
        return new AppDeploymentResult
        {
            State = HostDeploymentStatuses.Running,
            TargetPath = targetPath,
            RuntimeName = runtimeName
        };
    }

    public static AppDeploymentResult Failed(string? targetPath, string? runtimeName, string message)
    {
        return new AppDeploymentResult
        {
            State = HostDeploymentStatuses.Failed,
            TargetPath = targetPath,
            RuntimeName = runtimeName,
            ErrorMessage = message
        };
    }
}
