using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenModulePlatform.Web.Shared.Notifications;

public sealed record TopBarPushEventEnvelope(
    [property: JsonPropertyName("eventId")] long EventId,
    [property: JsonPropertyName("deduplicationKey")] string DeduplicationKey,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("targetKind")] string TargetKind,
    [property: JsonPropertyName("targetValue")] string? TargetValue,
    [property: JsonPropertyName("payload")] JsonElement? Payload)
{
    internal static TopBarPushEventEnvelope FromLeasedEvent(LeasedPushEvent pushEvent)
    {
        var target = PushEventTarget.FromJson(pushEvent.TargetType, pushEvent.TargetUserId, pushEvent.TargetJson);
        return new TopBarPushEventEnvelope(
            pushEvent.PushEventId,
            string.IsNullOrWhiteSpace(pushEvent.DeduplicationKey)
                ? pushEvent.PushEventId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : pushEvent.DeduplicationKey,
            pushEvent.EventCategory,
            target.Kind,
            target.Values.Count == 1 ? target.Values[0] : null,
            ParsePayload(pushEvent.PayloadJson));
    }

    private static JsonElement? ParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.Clone();
    }
}
