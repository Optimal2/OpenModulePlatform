namespace OpenModulePlatform.Web.Shared.Notifications;

public sealed class PushEventDispatcherOptions
{
    public const string SectionName = "PushEvents:Dispatcher";

    public bool Enabled { get; set; }

    public int BatchSize { get; set; } = 50;

    public int PollingIntervalSeconds { get; set; } = 2;

    public int LeaseSeconds { get; set; } = 30;

    public int RetryDelaySeconds { get; set; } = 10;

    public int MaxErrorMessageLength { get; set; } = 2048;

    public bool CleanupEnabled { get; set; } = true;

    public int CleanupIntervalMinutes { get; set; } = 15;

    public int CleanupBatchSize { get; set; } = 500;

    public int DispatchedRetentionDays { get; set; } = 7;

    public int FailedRetentionDays { get; set; } = 30;

    internal int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 200);

    internal int EffectivePollingIntervalSeconds => Math.Clamp(PollingIntervalSeconds, 1, 60);

    internal int EffectiveLeaseSeconds => Math.Clamp(LeaseSeconds, 5, 300);

    internal int EffectiveRetryDelaySeconds => Math.Clamp(RetryDelaySeconds, 1, 3600);

    internal int EffectiveMaxErrorMessageLength => Math.Clamp(MaxErrorMessageLength, 200, 2048);

    internal int EffectiveCleanupIntervalMinutes => Math.Clamp(CleanupIntervalMinutes, 1, 1440);

    internal int EffectiveCleanupBatchSize => Math.Clamp(CleanupBatchSize, 1, 5000);

    internal int EffectiveDispatchedRetentionDays => Math.Clamp(DispatchedRetentionDays, 1, 3650);

    internal int EffectiveFailedRetentionDays => Math.Clamp(FailedRetentionDays, 1, 3650);
}
