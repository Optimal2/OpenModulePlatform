using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContextGuardModule.Models;
using OpenModulePlatform.Web.ContextGuardModule.Options;
using OpenModulePlatform.Web.ContextGuardModule.Stores;

namespace OpenModulePlatform.Web.ContextGuardModule.Services;

public sealed class ContextGuardService(
    IHandoffStore handoffStore,
    IContextStore contextStore,
    IOptionsMonitor<ContextGuardOptions> options,
    ILogger<ContextGuardService> logger)
{
    public ContextGuardOptions OptionsSnapshot => options.CurrentValue;

    public HandoffCreateResult Receive(
        string profileKey,
        IReadOnlyList<ContextGuardField> allowedFields,
        int droppedFieldCount)
    {
        var profile = GetProfile(profileKey);
        var correlation = GetCorrelation(profile, allowedFields);
        var result = handoffStore.Create(
            profileKey,
            HashCorrelation(profileKey, correlation),
            allowedFields,
            droppedFieldCount,
            TimeSpan.FromSeconds(options.CurrentValue.HandoffTtlSeconds),
            options.CurrentValue.MaxPendingHandoffs);

        if (result.Status == HandoffCreateStatus.CapacityExceeded)
        {
            logger.LogWarning(
                "Context Guard rejected hand-off because the pending store is full. ProfileKey={ProfileKey}, DroppedFieldCount={DroppedFieldCount}",
                profileKey,
                droppedFieldCount);
        }
        else
        {
            logger.LogInformation(
                "Context Guard accepted hand-off. ProfileKey={ProfileKey}, FieldCount={FieldCount}, DroppedFieldCount={DroppedFieldCount}",
                profileKey,
                allowedFields.Count,
                droppedFieldCount);
        }

        return result;
    }

    public RedeemResult Redeem(string key, int userId, string clientId)
    {
        var redemption = handoffStore.Redeem(key);
        if (redemption.Status != HandoffRedemptionStatus.Success || redemption.Payload is null)
        {
            logger.LogInformation(
                "Context Guard hand-off redemption failed. OmpUserId={OmpUserId}, Outcome={Outcome}",
                userId,
                redemption.Status);
            return new RedeemResult(redemption.Status, null);
        }

        var current = options.CurrentValue;
        var payload = redemption.Payload;
        var context = contextStore.Create(
            userId,
            clientId,
            payload.ProfileKey,
            payload.CorrelationHash,
            current.Scope,
            TimeSpan.FromSeconds(current.HeartbeatTimeoutSeconds));
        logger.LogInformation(
            "Context Guard redeemed hand-off and created context. ProfileKey={ProfileKey}, ContextId={ContextId}, OmpUserId={OmpUserId}, FieldCount={FieldCount}",
            payload.ProfileKey,
            context.ContextId,
            userId,
            payload.Fields.Count);
        return new RedeemResult(HandoffRedemptionStatus.Success, new RedeemedContext(context, payload.Fields));
    }

    public ContextStateResult Heartbeat(Guid contextId, int userId)
    {
        var result = contextStore.Heartbeat(
            contextId,
            userId,
            TimeSpan.FromSeconds(options.CurrentValue.HeartbeatTimeoutSeconds));
        if (result.State != ContextLifecycle.Current)
        {
            logger.LogInformation(
                "Context Guard heartbeat refused. ContextId={ContextId}, OmpUserId={OmpUserId}, Outcome={Outcome}",
                contextId,
                userId,
                result.State);
        }

        return result;
    }

    public ContextStateResult GetState(Guid contextId, int userId)
        => contextStore.GetState(
            contextId,
            userId,
            TimeSpan.FromSeconds(options.CurrentValue.HeartbeatTimeoutSeconds));

    public int End(string profileKey, string correlationValue)
    {
        var hash = HashCorrelation(profileKey, correlationValue);
        var pendingCount = handoffStore.EndByCorrelation(profileKey, hash);
        var contextCount = contextStore.EndByCorrelation(profileKey, hash);
        logger.LogInformation(
            "Context Guard processed end call. ProfileKey={ProfileKey}, PendingEndedCount={PendingEndedCount}, ContextEndedCount={ContextEndedCount}",
            profileKey,
            pendingCount,
            contextCount);
        return pendingCount + contextCount;
    }

    public ContextGuardProfileOptions GetProfile(string profileKey)
        => options.CurrentValue.Profiles[profileKey];

    public bool TryGetProfile(string profileKey, out ContextGuardProfileOptions? profile)
    {
        var found = options.CurrentValue.Profiles.TryGetValue(profileKey, out var configured);
        if (!found)
        {
            logger.LogWarning("Context Guard rejected an unknown profile. ProfileKey={ProfileKey}", profileKey);
        }

        profile = configured;
        return found;
    }

    public static string GetCorrelation(
        ContextGuardProfileOptions profile,
        IReadOnlyList<ContextGuardField> fields)
        => fields.LastOrDefault(field => string.Equals(
            field.Name,
            profile.CorrelationFieldName,
            StringComparison.Ordinal))?.Value ?? string.Empty;

    private static string HashCorrelation(string profileKey, string correlationValue)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(profileKey + "\n" + correlationValue)));
}

public sealed record RedeemedContext(GuardContext Context, IReadOnlyList<ContextGuardField> Fields);

public sealed record RedeemResult(HandoffRedemptionStatus Status, RedeemedContext? Redeemed);
