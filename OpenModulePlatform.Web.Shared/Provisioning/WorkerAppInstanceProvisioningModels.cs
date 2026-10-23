// File: OpenModulePlatform.Web.Shared/Provisioning/WorkerAppInstanceProvisioningModels.cs
namespace OpenModulePlatform.Web.Shared.Provisioning;

/// <summary>
/// Input for <see cref="WorkerAppInstanceProvisioningService.ProvisionWorkerAppInstanceAsync"/>.
/// </summary>
/// <remarks>
/// Everything that identifies platform rows (module instance, app, installation name and
/// artifact) is resolved by the service from platform metadata or from the optional
/// template instance. The caller only supplies the new instance's identity, the hosts and
/// where the module's worker reads its HostAgent RPC settings.
/// </remarks>
public sealed class WorkerAppInstanceProvisioningRequest
{
    /// <summary>Module that owns the worker app, for example <c>example_module</c>.</summary>
    public string ModuleKey { get; init; } = "";

    /// <summary>Worker app key inside the module.</summary>
    public string AppKey { get; init; } = "";

    /// <summary>Key of the logical app instance; unique inside its module instance.</summary>
    public string AppInstanceKey { get; init; } = "";

    /// <summary>Display name of the logical app instance; worker names append the host name.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Optional description stored on a newly created app instance.</summary>
    public string? Description { get; init; }

    /// <summary>One worker instance is provisioned per host. One host is a standalone server,
    /// several hosts are the members behind a load balancer.</summary>
    public IReadOnlyList<Guid> HostIds { get; init; } = [];

    /// <summary>
    /// Optional existing app instance of the same app to copy the module instance, installation
    /// name and artifact from. Its configuration and IDs are never copied.
    /// </summary>
    public Guid? TemplateAppInstanceId { get; init; }

    /// <summary>
    /// Optional module instance. Required only when the module has more than one enabled
    /// module instance and no template is given.
    /// </summary>
    public Guid? ModuleInstanceId { get; init; }

    /// <summary>
    /// Colon-separated configuration path where the worker reads its HostAgent RPC settings,
    /// for example <c>MyModule:HostAgentRpc</c>. The service writes
    /// <c>Enabled</c>, <c>PipeName</c> and <c>TimeoutSeconds</c> under that path.
    /// </summary>
    public string HostAgentRpcConfigurationPath { get; init; } = "";

    /// <summary>
    /// RPC timeout written when the host's platform data does not state one.
    /// </summary>
    public int DefaultHostAgentRpcTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Optional module permission that authorizes the caller besides the platform administrator
    /// permission. It is honoured only when an app of the same module declares it in
    /// <c>omp.AppPermissions</c>, so one module cannot authorize provisioning in another.
    /// </summary>
    public string? ModuleAdministrationPermission { get; init; }
}

/// <summary>Outcome of a successful provisioning call.</summary>
/// <param name="AppInstanceId">The logical app instance that now exists.</param>
/// <param name="AppInstanceCreated">False when an earlier call already created it and it was reused.</param>
/// <param name="ModuleInstanceId">Resolved module instance.</param>
/// <param name="AppId">Resolved worker app.</param>
/// <param name="ArtifactId">Artifact bound to the app instance and to new workers.</param>
/// <param name="InstallationName">Installation name of the app instance.</param>
/// <param name="Workers">One entry per requested host, in request order.</param>
public sealed record WorkerAppInstanceProvisioningResult(
    Guid AppInstanceId,
    bool AppInstanceCreated,
    Guid ModuleInstanceId,
    int AppId,
    int? ArtifactId,
    string? InstallationName,
    IReadOnlyList<ProvisionedWorkerInstance> Workers);

/// <summary>A worker instance for one requested host.</summary>
/// <param name="WorkerInstanceId">The worker row.</param>
/// <param name="HostId">The host it runs on.</param>
/// <param name="HostKey">The host key it was derived from.</param>
/// <param name="WorkerInstanceKey">The worker key.</param>
/// <param name="Created">False when the worker already existed and was left unchanged.</param>
/// <param name="HostAgentRpcPipeName">The pipe written to a created worker; null for a reused worker.</param>
public sealed record ProvisionedWorkerInstance(
    Guid WorkerInstanceId,
    Guid HostId,
    string HostKey,
    string WorkerInstanceKey,
    bool Created,
    string? HostAgentRpcPipeName);

/// <summary>Why a provisioning call was refused. Nothing is written for any of these.</summary>
public enum WorkerAppInstanceProvisioningError
{
    /// <summary>The caller lacks the platform administrator permission and a valid module permission.</summary>
    Unauthorized,

    /// <summary>The request itself is malformed (empty keys, no hosts, duplicate hosts, bad path).</summary>
    InvalidRequest,

    /// <summary>The module or app does not exist or is disabled.</summary>
    AppNotFound,

    /// <summary>The app is not a worker app.</summary>
    NotAWorkerApp,

    /// <summary>The module instance is missing, disabled or cannot be chosen unambiguously.</summary>
    ModuleInstanceUnresolved,

    /// <summary>The template instance is missing or belongs to another app or module instance.</summary>
    TemplateMismatch,

    /// <summary>The installation name differs between existing instances; pass a template.</summary>
    InstallationNameUnresolved,

    /// <summary>No enabled artifact of the app is compatible with the module definition.</summary>
    NoCompatibleArtifact,

    /// <summary>The template's artifact is disabled or not compatible.</summary>
    IncompatibleArtifact,

    /// <summary>A host does not exist.</summary>
    HostNotFound,

    /// <summary>A host is disabled.</summary>
    HostDisabled,

    /// <summary>A host belongs to another installation (omp.Instances row) than the module instance.</summary>
    HostOutsideInstallation,

    /// <summary>The platform has no HostAgent RPC settings for a host.</summary>
    HostAgentRpcUnavailable,

    /// <summary>The app instance key or a worker key is already used by a different row.</summary>
    KeyCollision,

    /// <summary>Another call held the provisioning lock for the same key for too long.</summary>
    Busy
}

/// <summary>
/// Raised when provisioning is refused. The transaction is rolled back before this is thrown,
/// so no app or worker instance from the call remains.
/// </summary>
public sealed class WorkerAppInstanceProvisioningException : InvalidOperationException
{
    public WorkerAppInstanceProvisioningException(WorkerAppInstanceProvisioningError error, string message)
        : base(message)
    {
        Error = error;
    }

    public WorkerAppInstanceProvisioningError Error { get; }
}
