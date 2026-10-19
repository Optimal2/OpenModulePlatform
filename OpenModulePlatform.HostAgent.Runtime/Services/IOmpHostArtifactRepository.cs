using Microsoft.Data.SqlClient;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.ModuleDefinitions;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Repository for host-agent artifact, lease, deployment, and runtime state operations.
/// Extracted as an interface to enable deterministic unit testing of <see cref="HostAgentEngine" />.
/// </summary>
public interface IOmpHostArtifactRepository
{
    string GetConfiguredConnectionString();

    Task<IReadOnlyDictionary<string, string?>> ReadDeploymentLockSettingsAsync(CancellationToken ct);

    Task<AppDeploymentLeaseResult> AcquireAppDeploymentLeaseAsync(
        string scopeKey, Guid hostId, string reason, int leaseSeconds, CancellationToken ct);

    Task ReleaseAppDeploymentLeaseAsync(string scopeKey, Guid leaseToken, string? reason, CancellationToken ct);

    Task<HostAgentLeaseResult> TryAcquireHostAgentLeaseAsync(
        string hostKey,
        string serviceName,
        string runtimeMode,
        bool forceTakeover,
        int leaseSeconds,
        CancellationToken ct);

    Task ReleaseHostAgentLeaseAsync(Guid hostId, string serviceName, Guid? leaseToken, CancellationToken ct);

    Task<bool> RenewHostAgentLeaseAsync(Guid hostId, Guid leaseToken, int leaseSeconds, CancellationToken ct);

    Task PublishHostAgentRuntimeStateAsync(
        Guid hostId,
        HostAgentProcessContext process,
        string runtimeMode,
        int? artifactId,
        string? installPath,
        bool isActive,
        string? statusMessage,
        CancellationToken ct,
        bool preserveExistingStatusMessage = false);

    Task MarkHostAgentQuiescedAsync(Guid hostId, string serviceName, CancellationToken ct);

    Task TouchHostHeartbeatAsync(string hostKey, CancellationToken ct);

    Task<int> GetEnabledHostCountAsync(CancellationToken ct);

    Task<TemplateMaterializationResult> MaterializeTemplatesForHostAsync(
        string hostKey,
        int? hostTemplateId,
        CancellationToken ct);

    Task<IReadOnlyList<ArtifactDescriptor>> GetDesiredArtifactsAsync(
        string hostKey,
        bool includeAppInstanceArtifacts,
        bool includeExplicitRequirements,
        int maxArtifacts,
        CancellationToken ct);

    Task<ArtifactDescriptor?> GetArtifactByIdAsync(
        string hostKey,
        int artifactId,
        string? desiredLocalPath,
        CancellationToken ct);

    Task PublishResultAsync(ArtifactDescriptor artifact, ArtifactProvisioningResult result, CancellationToken ct);

    Task<IReadOnlyList<DeploymentRuntimeRecoveryCandidate>> GetWebAppDeploymentRecoveryCandidatesAsync(
        string hostKey,
        CancellationToken ct);

    Task<IReadOnlyList<DeploymentRuntimeRecoveryCandidate>> GetServiceAppDeploymentRecoveryCandidatesAsync(
        string hostKey,
        CancellationToken ct);

    Task<IReadOnlyList<DeploySetConsistencyCheckResult>> GetDeploySetConsistencyResultsAsync(
        string hostKey,
        IReadOnlyList<int> artifactIds,
        CancellationToken ct);

    Task<IReadOnlyList<WebAppDeploymentDescriptor>> GetDesiredWebAppDeploymentsAsync(
        string hostKey,
        int maxDeployments,
        CancellationToken ct);

    Task<IReadOnlyList<ServiceAppDeploymentDescriptor>> GetDesiredServiceAppDeploymentsAsync(
        string hostKey,
        int maxDeployments,
        CancellationToken ct);

    /// <summary>
    /// Service-app instances targeted at this host that are switched off
    /// (IsEnabled = 0 or DesiredState in (0, 2)) but still have a recorded runtime deployment
    /// (runtime name and target path) on this host. The deploy loop no longer manages
    /// these instances, so without this query their Windows services stay installed --
    /// and keep running -- forever.
    /// </summary>
    Task<IReadOnlyList<DisabledServiceAppServiceDescriptor>> GetDisabledServiceAppServicesAsync(
        string hostKey,
        int maxDeployments,
        CancellationToken ct);

    /// <summary>Records a confirmed stop or removal without overwriting a newer desired deployment.</summary>
    Task PublishInactiveServiceAppResultAsync(
        string hostKey,
        DisabledServiceAppServiceDescriptor candidate,
        bool removed,
        CancellationToken ct);

    /// <summary>
    /// Every app instance's deployed runtime name and target path on this host, web and
    /// service alike, with no cycle cap applied.
    /// </summary>
    Task<IReadOnlyList<HostRuntimeFootprint>> GetHostRuntimeFootprintsAsync(
        string hostKey,
        CancellationToken ct);

    Task<IReadOnlyList<ArtifactConfigurationFileDescriptor>> GetArtifactConfigurationFilesAsync(
        int artifactId,
        string hostKey,
        CancellationToken ct);

    /// <summary>
    /// Enabled config overlays that match every selector for the artifact except
    /// their artifactVersion minimum (ADR 0006), surfaced as deployment warnings.
    /// </summary>
    Task<IReadOnlyList<ConfigOverlayVersionSkip>> GetVersionSkippedConfigOverlaysAsync(
        int artifactId,
        string hostKey,
        CancellationToken ct);

    Task<IReadOnlyList<string>> GetRequiredConfigRootSectionsAsync(
        int artifactId,
        CancellationToken ct);

    Task PublishAppDeploymentResultAsync(
        WebAppDeploymentDescriptor deployment,
        AppDeploymentResult result,
        CancellationToken ct);

    Task PublishAppDeploymentResultAsync(
        ServiceAppDeploymentDescriptor deployment,
        AppDeploymentResult result,
        CancellationToken ct);

    Task<HostDeploymentWorkItem?> TryClaimNextHostDeploymentAsync(
        string hostKey,
        string serviceName,
        int leaseSeconds,
        int maxAttempts,
        CancellationToken ct);

    /// <summary>False when the lease was lost and the row was not updated (R6-D8).</summary>
    Task<bool> CompleteHostDeploymentAsync(
        long hostDeploymentId,
        Guid leaseToken,
        bool succeeded,
        string outcomeMessage,
        CancellationToken ct);

    Task<bool> RenewHostDeploymentLeaseAsync(
        long hostDeploymentId,
        Guid leaseToken,
        int leaseSeconds,
        CancellationToken ct);

    Task<bool> EnqueueMaintenanceScanJobAsync(
        string hostKey,
        string? requestedBy,
        CancellationToken ct);

    Task<IReadOnlyList<OrphanHostCandidate>> GetOrphanHostCandidatesAsync(
        Guid currentHostId,
        int maxCandidates,
        CancellationToken ct);

    Task<WebAppHealthProbeResult> UpsertWebAppHealthStateAsync(
        Guid hostId,
        WebAppHealthProbeResult probe,
        CancellationToken ct);

    Task RecordWebAppHealthActionAsync(
        Guid hostId,
        string healthKey,
        string message,
        CancellationToken ct);
}

/// <summary>
/// The repository surface <see cref="ArtifactZipImportService" /> depends on. Extracted so
/// the import gates (identity, duplicate content, configuration continuity) can be exercised
/// against a deterministic fake instead of only through a SQL Server fixture. Internal
/// because several members expose internal SQL repair types.
/// </summary>
internal interface IArtifactZipImportRepository
{
    Task<ArtifactContentHashGap> GetArtifactContentHashGapAsync(CancellationToken ct);

    Task<(int TemplateAppRowsUpdated, int AppInstanceRowsUpdated, int WorkerInstanceRowsUpdated, int ConfigurationRowsCarried, string? ConfigurationCarryMessage)> ApplyLatestModuleArtifactsAsync(
        string moduleKey,
        CancellationToken ct);

    Task<(int DocumentId, bool Created, bool Replaced, bool WasIdentical)> SaveImportedHostConfigurationAsync(
        PortableHostConfigurationDocument input,
        bool replaceExisting,
        CancellationToken ct);

    Task<(int DocumentId, bool Created, bool Replaced, bool WasIdentical)> SaveImportedConfigOverlayAsync(
        PortableConfigOverlayDocument input,
        bool replaceExisting,
        CancellationToken ct);

    Task<(int CreatedCount, int UpdatedCount, int SkippedCount, int PermissionRowCount)> SaveImportedDashboardWidgetsAsync(
        PortableDashboardWidgetPackage input,
        CancellationToken ct);

    Task RecordDashboardWidgetImportFindingsAsync(
        IReadOnlyCollection<string> importedWidgetKeys,
        IReadOnlyCollection<PortableDashboardWidgetSkip> skippedWidgets,
        string sourceName,
        CancellationToken ct);

    Task<(int DataDocumentCount, int InsertedBinaryDataCount, int ReusedBinaryDataCount)> SaveImportedWidgetRuntimeDataAsync(
        PortableWidgetRuntimeDataPackage input,
        CancellationToken ct);

    Task<ModuleDefinitionSaveResult> SaveImportedModuleDefinitionAsync(
        ModuleDefinitionImportDocument input,
        bool replaceExisting,
        CancellationToken ct);

    Task<string?> GetAppliedModuleDefinitionVersionAsync(string moduleKey, CancellationToken ct);

    Task<int> ExecuteImportedModuleDefinitionSqlRepairsAsync(int moduleDefinitionDocumentId, CancellationToken ct);

    Task<int> ExecuteImportedModuleDefinitionSqlRepairsAsync(
        int moduleDefinitionDocumentId,
        IReadOnlySet<string>? scriptKeysToRepair,
        CancellationToken ct,
        SqlConnection conn);

    Task<bool> ApplyImportedModuleDefinitionAsync(int moduleDefinitionDocumentId, CancellationToken ct);

    Task<ModuleDefinitionSqlRepairScope> BeginModuleDefinitionSqlRepairAsync(CancellationToken ct);

    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetMissingRequiredObjectsByScriptKeyAsync(
        string definitionJson,
        CancellationToken ct);

    Task<IReadOnlyList<string>> GetPendingDefinitionScriptsAsync(int documentId, SqlConnection conn, CancellationToken ct);

    Task<int> InsertMissingPortalAppEntriesAsync(CancellationToken ct, Action<int>? onAttempt = null);

    Task<ArtifactZipImportAppDescriptor?> ResolveArtifactZipImportAppAsync(
        string moduleKey,
        string appKey,
        CancellationToken ct);

    Task<ArtifactCompatibilitySlot> RequireCompatibleArtifactSlotAsync(
        int appId,
        string version,
        string packageType,
        string targetName,
        CancellationToken ct);

    Task<ArtifactZipImportDuplicateInfo?> FindImportedArtifactByIdentityAsync(
        int appId,
        string version,
        string packageType,
        string targetName,
        CancellationToken ct);

    /// <summary>
    /// An artifact whose content hash equals <paramref name="sha256" />. When several do, a row
    /// of the same component (<paramref name="appId" /> and <paramref name="packageType" />) is
    /// preferred, so an unrelated older row with the same bytes cannot hide the same-component
    /// match the empty-diff gate accepts.
    /// </summary>
    Task<ArtifactZipImportDuplicateInfo?> FindImportedArtifactBySha256Async(
        string sha256,
        int appId,
        string packageType,
        CancellationToken ct);

    Task UpdateImportedArtifactMetadataAsync(
        int artifactId,
        string relativePath,
        string sha256,
        CancellationToken ct);

    /// <summary>
    /// Registers a new artifact identity and writes its configuration rows in ONE transaction:
    /// the package files plus the carry-forward of operator edits, or -- when the package ships
    /// none and <paramref name="copyFromContinuitySource" /> is set -- the continuity copy.
    /// Null when the identity already exists (a concurrent import won). A failure or a process
    /// death anywhere inside leaves no artifact row, so the retry registers again and carries
    /// forward instead of taking the existing-identity path that never does.
    /// </summary>
    Task<ImportedArtifactRegistration?> RegisterImportedArtifactWithConfigurationAsync(
        int appId,
        string version,
        string packageType,
        string targetName,
        string relativePath,
        string sha256,
        IReadOnlyList<ArtifactPackageConfigurationFile> configurationFiles,
        bool copyFromContinuitySource,
        CancellationToken ct);

    /// <summary>
    /// The existing-identity replacement. Resumes the carry-forward when the identity has no
    /// configuration rows yet (an older interrupted import); otherwise a plain replace.
    /// </summary>
    Task<(int ReplacedCount, ArtifactConfigurationCarryForwardResult CarryForward)> ReplaceArtifactConfigurationFilesResumingCarryForwardAsync(
        int artifactId,
        IReadOnlyList<ArtifactPackageConfigurationFile> configurationFiles,
        CancellationToken ct);

    /// <summary>
    /// Resumes an interrupted zero-configuration import: copies from the continuity source
    /// when the identity has no configuration rows yet, NULL when rows already exist.
    /// </summary>
    Task<ArtifactConfigurationContinuityCopyResult?> CopyConfigurationFilesFromContinuitySourceIfEmptyAsync(
        int artifactId,
        CancellationToken ct);

    Task<(int TemplateAppRowsUpdated, int AppInstanceRowsUpdated, int WorkerInstanceRowsUpdated, int ConfigurationRowsCarried, string? ConfigurationCarryMessage)> ApplyImportedArtifactToMatchingApplicationsAsync(
        int artifactId,
        CancellationToken ct);

    Task<int> ApplyImportedHostAgentArtifactToCurrentHostAsync(
        int artifactId,
        string hostKey,
        string? serviceNamePrefix,
        string? installRoot,
        CancellationToken ct);

    Task<WorkerHostVersionState> GetSelectedAndProvisionedWorkerHostVersionsAsync(
        string hostKey,
        string componentKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of <see cref="IArtifactZipImportRepository.RegisterImportedArtifactWithConfigurationAsync" />.
/// <paramref name="ContinuityCopy" /> is set only when the package shipped no configuration files
/// and the continuity copy ran.
/// </summary>
public sealed record ImportedArtifactRegistration(
    int ArtifactId,
    int ConfigurationFileCount,
    ArtifactConfigurationCarryForwardResult CarryForward,
    ArtifactConfigurationContinuityCopyResult? ContinuityCopy);
