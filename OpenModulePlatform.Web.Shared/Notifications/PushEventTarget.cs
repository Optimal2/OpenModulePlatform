using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenModulePlatform.Web.Shared.Notifications;

internal sealed record PushEventTarget(string Kind, IReadOnlyList<string> Values)
{
    public static PushEventTarget FromJson(string targetType, int? targetUserId, string targetJson)
    {
        if (!string.IsNullOrWhiteSpace(targetJson))
        {
            var dto = JsonSerializer.Deserialize<PushEventTargetJson>(targetJson);
            if (dto is not null && !string.IsNullOrWhiteSpace(dto.Kind))
            {
                return new PushEventTarget(
                    dto.Kind.Trim().ToLowerInvariant(),
                    (dto.Ids ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray());
            }
        }

        if (string.Equals(targetType, "user", StringComparison.OrdinalIgnoreCase)
            && targetUserId is int userId
            && userId > 0)
        {
            return new PushEventTarget("user", [userId.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        return new PushEventTarget(targetType.Trim().ToLowerInvariant(), []);
    }

    private sealed record PushEventTargetJson(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("ids")] string[]? Ids);
}
