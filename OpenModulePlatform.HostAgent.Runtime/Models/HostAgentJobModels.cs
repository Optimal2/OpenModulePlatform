namespace OpenModulePlatform.HostAgent.Runtime.Models;

public static class HostAgentJobTypes
{
    public const string ArtifactRetentionCleanup = "ArtifactRetentionCleanup";

    public const string ArtifactCacheCleanup = "ArtifactCacheCleanup";

    public const string ArtifactStoreCleanup = "ArtifactStoreCleanup";
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
    int AttemptCount);

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
