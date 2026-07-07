namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class AppDeploymentResult
{
    public byte State { get; init; }

    public bool Applied { get; init; }

    public string? TargetPath { get; init; }

    public string? RuntimeName { get; init; }

    public string? ErrorMessage { get; init; }

    public bool IsSuccess => State == HostDeploymentStatuses.Succeeded;

    public string? CredentialAutomationMode { get; init; }

    public string? DesiredRuntimeIdentity { get; init; }

    public string? ActualRuntimeIdentity { get; init; }

    public string? IdentityCheckStatus { get; init; }

    public bool ClearIdentityRepairRequest { get; init; }

    public string? DiagnosticWarningMessage { get; init; }

    public string? EffectiveOmpAuthCookieName { get; init; }

    public string? EffectiveOmpAuthApplicationName { get; init; }

    public string? EffectiveOmpAuthDataProtectionKeyPath { get; init; }

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

    public static AppDeploymentResult Warning(string? targetPath, string? runtimeName, string message)
    {
        return new AppDeploymentResult
        {
            State = HostDeploymentStatuses.Warning,
            TargetPath = targetPath,
            RuntimeName = runtimeName,
            ErrorMessage = message
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

    public AppDeploymentResult WithIdentityCheck(
        string automationMode,
        string? desiredRuntimeIdentity,
        string? actualRuntimeIdentity,
        string identityCheckStatus,
        bool clearIdentityRepairRequest = false)
    {
        return new AppDeploymentResult
        {
            State = State,
            Applied = Applied,
            TargetPath = TargetPath,
            RuntimeName = RuntimeName,
            ErrorMessage = ErrorMessage,
            CredentialAutomationMode = automationMode,
            DesiredRuntimeIdentity = desiredRuntimeIdentity,
            ActualRuntimeIdentity = actualRuntimeIdentity,
            IdentityCheckStatus = identityCheckStatus,
            ClearIdentityRepairRequest = clearIdentityRepairRequest,
            DiagnosticWarningMessage = DiagnosticWarningMessage,
            EffectiveOmpAuthCookieName = EffectiveOmpAuthCookieName,
            EffectiveOmpAuthApplicationName = EffectiveOmpAuthApplicationName,
            EffectiveOmpAuthDataProtectionKeyPath = EffectiveOmpAuthDataProtectionKeyPath
        };
    }

    public AppDeploymentResult WithDiagnosticWarning(string? message)
    {
        return new AppDeploymentResult
        {
            State = State,
            Applied = Applied,
            TargetPath = TargetPath,
            RuntimeName = RuntimeName,
            ErrorMessage = ErrorMessage,
            CredentialAutomationMode = CredentialAutomationMode,
            DesiredRuntimeIdentity = DesiredRuntimeIdentity,
            ActualRuntimeIdentity = ActualRuntimeIdentity,
            IdentityCheckStatus = IdentityCheckStatus,
            ClearIdentityRepairRequest = ClearIdentityRepairRequest,
            DiagnosticWarningMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
            EffectiveOmpAuthCookieName = EffectiveOmpAuthCookieName,
            EffectiveOmpAuthApplicationName = EffectiveOmpAuthApplicationName,
            EffectiveOmpAuthDataProtectionKeyPath = EffectiveOmpAuthDataProtectionKeyPath
        };
    }

    public AppDeploymentResult WithEffectiveOmpAuth(
        string? cookieName,
        string? applicationName,
        string? dataProtectionKeyPath)
    {
        return new AppDeploymentResult
        {
            State = State,
            Applied = Applied,
            TargetPath = TargetPath,
            RuntimeName = RuntimeName,
            ErrorMessage = ErrorMessage,
            CredentialAutomationMode = CredentialAutomationMode,
            DesiredRuntimeIdentity = DesiredRuntimeIdentity,
            ActualRuntimeIdentity = ActualRuntimeIdentity,
            IdentityCheckStatus = IdentityCheckStatus,
            ClearIdentityRepairRequest = ClearIdentityRepairRequest,
            DiagnosticWarningMessage = DiagnosticWarningMessage,
            EffectiveOmpAuthCookieName = string.IsNullOrWhiteSpace(cookieName) ? null : cookieName.Trim(),
            EffectiveOmpAuthApplicationName = string.IsNullOrWhiteSpace(applicationName) ? null : applicationName.Trim(),
            EffectiveOmpAuthDataProtectionKeyPath = string.IsNullOrWhiteSpace(dataProtectionKeyPath) ? null : dataProtectionKeyPath.Trim()
        };
    }
}
