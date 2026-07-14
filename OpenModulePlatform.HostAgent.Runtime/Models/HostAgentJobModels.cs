namespace OpenModulePlatform.HostAgent.Runtime.Models;

public static class HostAgentJobTypes
{
    public const string ArtifactRetentionCleanup = "ArtifactRetentionCleanup";

    public const string ArtifactCacheCleanup = "ArtifactCacheCleanup";

    public const string ArtifactStoreCleanup = "ArtifactStoreCleanup";

    public const string MaintenanceScan = "MaintenanceScan";

    public const string MaintenanceCleanup = "MaintenanceCleanup";

    public const string WebAppHealthProbe = "WebAppHealthProbe";

    public const string RecycleWebAppAppPool = "RecycleWebAppAppPool";

    public const string CollectWebAppLogs = "CollectWebAppLogs";
}

public static class HostAgentJobStatuses
{
    public const byte Pending = 0;
    public const byte Running = 1;
    public const byte Succeeded = 2;
    public const byte Failed = 3;
    public const byte Warning = 4;
    public const byte Cancelled = 5;
}

public sealed record HostAgentJobWorkItem(
    long HostAgentJobId,
    Guid? HostId,
    string JobType,
    string? PayloadJson,
    string? RequestedBy,
    int AttemptCount,
    Guid LeaseToken);

public sealed class ArtifactRetentionCleanupJobPayload
{
    public int SchemaVersion { get; set; } = 1;

    public int MaxVersionsToKeep { get; set; } = 5;
}

public sealed class ArtifactRetentionCleanupExecutionResult
{
    public IReadOnlyList<ArtifactRetentionCleanupDeletedArtifact> DeletedArtifacts { get; init; } = [];

    public IReadOnlyList<ArtifactStoreCleanupJobEntry> ArtifactStoreEntries { get; init; } = [];

    public int ArtifactStoreEntryCount { get; init; }

    public int HostCacheEntryCount { get; init; }

    public int CreatedHostAgentJobCount { get; init; }
}

public sealed class ArtifactRetentionCleanupJobResult
{
    public int DeletedArtifactCount { get; set; }

    public int ArtifactStoreEntryCount { get; set; }

    public int HostCacheEntryCount { get; set; }

    public int CreatedHostAgentJobCount { get; set; }

    public ArtifactStoreCleanupJobResult ArtifactStoreCleanup { get; set; } = new();

    public List<ArtifactRetentionCleanupDeletedArtifact> DeletedArtifacts { get; set; } = [];
}

public sealed class ArtifactRetentionCleanupDeletedArtifact
{
    public int ArtifactId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    public string AppKey { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public string? RelativePath { get; set; }

    public DateTime CreatedUtc { get; set; }

    public int RetentionRank { get; set; }

    public int TotalVersions { get; set; }
}

public sealed class ArtifactCacheCleanupJobPayload
{
    public int SchemaVersion { get; set; } = 1;

    public List<ArtifactCacheCleanupJobEntry> ArtifactCacheEntries { get; set; } = [];
}

public sealed class ArtifactCacheCleanupJobEntry
{
    public int ArtifactId { get; set; }

    public string Version { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public string? LocalPath { get; set; }

    public string? CacheRelativePath { get; set; }

    public string? ContentSha256 { get; set; }
}

public sealed class ArtifactCacheCleanupJobResult
{
    public int DeletedCount { get; set; }

    public int MissingCount { get; set; }

    public int SkippedCount { get; set; }

    public int ErrorCount { get; set; }

    public List<ArtifactCacheCleanupEntryResult> Entries { get; set; } = [];
}

public sealed class ArtifactCacheCleanupEntryResult
{
    public int ArtifactId { get; set; }

    public string? LocalPath { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string? Message { get; set; }
}

public sealed class ArtifactStoreCleanupJobPayload
{
    public int SchemaVersion { get; set; } = 1;

    public List<ArtifactStoreCleanupJobEntry> ArtifactStoreEntries { get; set; } = [];
}

public sealed class ArtifactStoreCleanupJobEntry
{
    public int ArtifactId { get; set; }

    public string Version { get; set; } = string.Empty;

    public string PackageType { get; set; } = string.Empty;

    public string? TargetName { get; set; }

    public string? RelativePath { get; set; }
}

public sealed class ArtifactStoreCleanupJobResult
{
    public int DeletedCount { get; set; }

    public int MissingCount { get; set; }

    public int SkippedCount { get; set; }

    public int ErrorCount { get; set; }

    public List<ArtifactStoreCleanupEntryResult> Entries { get; set; } = [];
}

public sealed class ArtifactStoreCleanupEntryResult
{
    public int ArtifactId { get; set; }

    public string? RelativePath { get; set; }

    public string? StorePath { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string? Message { get; set; }
}

public static class MaintenanceScanScopes
{
    public const string Global = "Global";

    public const string Host = "Host";
}

public static class MaintenanceFindingStatuses
{
    public const byte Open = 0;
    public const byte Ignored = 1;
    public const byte CleanupQueued = 2;
    public const byte Cleaned = 3;
    public const byte Failed = 4;
    public const byte Skipped = 5;
}

public static class MaintenanceTargetKinds
{
    public const string DatabaseRow = "DatabaseRow";
    public const string Directory = "Directory";
    public const string File = "File";
    public const string WindowsService = "WindowsService";
    public const string IisApplication = "IisApplication";
    public const string IisAppPool = "IisAppPool";
    public const string OrphanHost = "OrphanHost";
}

public sealed class MaintenanceScanJobPayload
{
    public int SchemaVersion { get; set; } = 1;

    public string Scope { get; set; } = MaintenanceScanScopes.Host;
}

public sealed class MaintenanceCleanupJobPayload
{
    public int SchemaVersion { get; set; } = 1;

    public List<long> FindingIds { get; set; } = [];
}

public sealed class MaintenanceFindingUpsert
{
    public string FindingKey { get; set; } = string.Empty;

    public string Scope { get; set; } = MaintenanceScanScopes.Host;

    public Guid? HostId { get; set; }

    public string Category { get; set; } = string.Empty;

    public string TargetKind { get; set; } = string.Empty;

    public string TargetIdentifier { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public string? RecommendedAction { get; set; }

    public string? SafetyNotes { get; set; }

    public string? ActionJson { get; set; }

    public byte Severity { get; set; } = 1;

    public byte Confidence { get; set; } = 80;
}

public sealed class MaintenanceScanJobResult
{
    public string Scope { get; set; } = string.Empty;

    public int FindingCount { get; set; }

    public List<string> FindingKeys { get; set; } = [];
}

public sealed class MaintenanceCleanupJobResult
{
    public int CleanedCount { get; set; }

    public int MissingCount { get; set; }

    public int SkippedCount { get; set; }

    public int ErrorCount { get; set; }

    public List<MaintenanceCleanupEntryResult> Entries { get; set; } = [];
}

public sealed class MaintenanceCleanupEntryResult
{
    public long MaintenanceFindingId { get; set; }

    public string TargetKind { get; set; } = string.Empty;

    public string TargetIdentifier { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string? Message { get; set; }
}

public sealed class MaintenanceFindingCleanupEntry
{
    public long MaintenanceFindingId { get; set; }

    public Guid? HostId { get; set; }

    public string TargetKind { get; set; } = string.Empty;

    public string TargetIdentifier { get; set; } = string.Empty;

    public string? ActionJson { get; set; }
}

public sealed class MaintenanceFindingAction
{
    public int SchemaVersion { get; set; } = 1;

    public string TargetKind { get; set; } = string.Empty;

    public Guid? HostId { get; set; }

    public string? ServiceName { get; set; }

    public string? Path { get; set; }

    public string? InstallRoot { get; set; }
}

public static class WebAppHealthStatuses
{
    public const byte Unknown = 0;
    public const byte Healthy = 1;
    public const byte Degraded = 2;
    public const byte Unhealthy = 3;
}

public sealed class WebAppHealthProbeJobPayload
{
    public int SchemaVersion { get; set; } = 1;

    public string HealthKey { get; set; } = "portal";

    public bool RecycleIfUnhealthy { get; set; }
}

public sealed class WebAppHealthProbeJobResult
{
    public string HealthKey { get; set; } = string.Empty;

    public string ProbeUrl { get; set; } = string.Empty;

    public byte Status { get; set; }

    public int? HttpStatusCode { get; set; }

    public int ConsecutiveFailures { get; set; }

    public string? Message { get; set; }

    public bool RecycledAppPool { get; set; }
}

public sealed class RecycleWebAppAppPoolJobPayload
{
    public int SchemaVersion { get; set; } = 1;

    public string HealthKey { get; set; } = "portal";

    public string AppPoolName { get; set; } = string.Empty;
}

public sealed class RecycleWebAppAppPoolJobResult
{
    public string HealthKey { get; set; } = string.Empty;

    public string AppPoolName { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

public sealed class CollectWebAppLogsJobPayload
{
    public int SchemaVersion { get; set; } = 1;

    public string HealthKey { get; set; } = "portal";

    public int MaxLines { get; set; } = 120;
}

public sealed class CollectWebAppLogsJobResult
{
    public string HealthKey { get; set; } = string.Empty;

    public string LogDirectory { get; set; } = string.Empty;

    public string? LogFile { get; set; }

    public int LineCount { get; set; }

    public string Content { get; set; } = string.Empty;

    public string? Message { get; set; }
}

public sealed class HostResourceSample
{
    public string SampleKey { get; set; } = string.Empty;

    public double SampleValue { get; set; }

    public DateTime SampledUtc { get; set; }

    public double? MinValue { get; set; }

    public double? MaxValue { get; set; }
}

public sealed class WebAppHealthProbeResult
{
    public string HealthKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ProbeUrl { get; set; } = string.Empty;

    public string AppPoolName { get; set; } = string.Empty;

    public byte Status { get; set; } = WebAppHealthStatuses.Unknown;

    public int? HttpStatusCode { get; set; }

    public int ConsecutiveFailures { get; set; }

    public DateTime? LastActionUtc { get; set; }

    public string? ResponseSummary { get; set; }

    public string? Error { get; set; }

    public bool IsHealthy => Status == WebAppHealthStatuses.Healthy;
}
