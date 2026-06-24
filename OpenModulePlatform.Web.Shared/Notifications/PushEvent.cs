using System.Text.Json;

namespace OpenModulePlatform.Web.Shared.Notifications;

public sealed record PushEvent(
    string EventCategory,
    string TargetType,
    int? TargetUserId = null,
    string? PayloadJson = null)
{
    public const int MaxEventCategoryLength = 80;
    public const int MaxPayloadJsonLength = 16 * 1024;

    public static PushEvent ForUser(int userId, string eventCategory, string? payloadJson = null)
        => new(eventCategory, PushEventTargetTypes.User, userId, payloadJson);

    public static PushEvent ForBroadcast(string eventCategory, string? payloadJson = null)
        => new(eventCategory, PushEventTargetTypes.Broadcast, null, payloadJson);

    public static PushEvent ForAuthenticatedUsers(string eventCategory, string? payloadJson = null)
        => new(eventCategory, PushEventTargetTypes.Authenticated, null, payloadJson);

    public PushEvent Normalize()
    {
        var category = NormalizeRequired(EventCategory, nameof(EventCategory), MaxEventCategoryLength);
        var targetType = NormalizeRequired(TargetType, nameof(TargetType), 40).ToLowerInvariant();

        if (!PushEventTargetTypes.IsSupported(targetType))
        {
            throw new ArgumentException(
                "Push event target type must be one of: user, broadcast, authenticated.",
                nameof(TargetType));
        }

        int? targetUserId = TargetUserId;
        if (targetType == PushEventTargetTypes.User)
        {
            if (targetUserId is null or <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(TargetUserId),
                    "User-targeted push events require an OMP user id greater than zero.");
            }
        }
        else
        {
            targetUserId = null;
        }

        var payloadJson = NormalizePayloadJson(PayloadJson);
        return this with
        {
            EventCategory = category,
            TargetType = targetType,
            TargetUserId = targetUserId,
            PayloadJson = payloadJson
        };
    }

    private static string NormalizeRequired(string? value, string parameterName, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Push event value is required.", parameterName);
        }

        if (normalized.Length > maxLength)
        {
            throw new ArgumentException(
                $"Push event value cannot exceed {maxLength} characters.",
                parameterName);
        }

        return normalized;
    }

    private static string? NormalizePayloadJson(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        var normalized = payloadJson.Trim();
        if (normalized.Length > MaxPayloadJsonLength)
        {
            throw new ArgumentException(
                $"Push event payload JSON cannot exceed {MaxPayloadJsonLength} characters.",
                nameof(PayloadJson));
        }

        try
        {
            using var _ = JsonDocument.Parse(normalized);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Push event payload must be valid JSON.", nameof(PayloadJson), ex);
        }

        return normalized;
    }
}

public static class PushEventTargetTypes
{
    public const string User = "user";
    public const string Broadcast = "broadcast";
    public const string Authenticated = "authenticated";

    public static bool IsSupported(string targetType)
        => string.Equals(targetType, User, StringComparison.Ordinal)
            || string.Equals(targetType, Broadcast, StringComparison.Ordinal)
            || string.Equals(targetType, Authenticated, StringComparison.Ordinal);
}

public static class PushEventCategories
{
    public const string TopBarNotificationStateChanged = "topbar.notification-state-changed";
}
