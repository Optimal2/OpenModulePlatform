namespace OpenModulePlatform.Web.ContextGuardModule.Models;

public sealed record ContextGuardField(string Name, string Value);

public enum HandoffRedemptionStatus
{
    Success,
    Unknown,
    Expired,
    AlreadyRedeemed,
    Ended
}

public sealed record HandoffPayload(
    string ProfileKey,
    string CorrelationHash,
    IReadOnlyList<ContextGuardField> Fields);

public sealed record HandoffRedemption(HandoffRedemptionStatus Status, HandoffPayload? Payload);

public enum HandoffCreateStatus
{
    Created,
    CapacityExceeded
}

public sealed record HandoffCreateResult(HandoffCreateStatus Status, string? Key, int DroppedFieldCount);

public sealed record GuardContext(
    Guid ContextId,
    int UserId,
    string ClientId,
    string ProfileKey,
    string CorrelationHash,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastHeartbeatUtc,
    ContextLifecycle Lifecycle);

public enum ContextLifecycle
{
    Current,
    Superseded,
    Ended
}

public sealed record ContextStateResult(ContextLifecycle State, GuardContext? Context);

public sealed record GuardPageModel(
    Guid ContextId,
    string ProfileKey,
    string ProfileDisplayName,
    string CorrelationValue,
    string TargetUrl,
    string Method,
    string Delivery,
    IReadOnlyList<ContextGuardField> Fields);
