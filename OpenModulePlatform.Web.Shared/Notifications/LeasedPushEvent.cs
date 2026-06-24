namespace OpenModulePlatform.Web.Shared.Notifications;

internal sealed record LeasedPushEvent(
    long PushEventId,
    string EventCategory,
    string TargetType,
    int? TargetUserId,
    string TargetJson,
    string? PayloadJson,
    string? DeduplicationKey,
    string? CorrelationKey,
    Guid LeaseToken,
    DateTime CreatedUtc);
