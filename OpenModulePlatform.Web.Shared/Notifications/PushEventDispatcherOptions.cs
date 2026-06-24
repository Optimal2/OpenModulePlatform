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

    internal int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 200);

    internal int EffectivePollingIntervalSeconds => Math.Clamp(PollingIntervalSeconds, 1, 60);

    internal int EffectiveLeaseSeconds => Math.Clamp(LeaseSeconds, 5, 300);

    internal int EffectiveRetryDelaySeconds => Math.Clamp(RetryDelaySeconds, 1, 3600);

    internal int EffectiveMaxErrorMessageLength => Math.Clamp(MaxErrorMessageLength, 200, 2048);
}
