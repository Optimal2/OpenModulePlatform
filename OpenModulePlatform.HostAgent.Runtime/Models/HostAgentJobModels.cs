namespace OpenModulePlatform.HostAgent.Runtime.Models;

public static class HostAgentJobTypes
{
    public const string ArtifactCacheCleanup = "ArtifactCacheCleanup";
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
    Guid HostId,
    string JobType,
    string? PayloadJson,
    int AttemptCount);

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
