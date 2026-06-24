using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenModulePlatform.EventPublisher;

public sealed record PushEvent(
    PushEventCategory Category,
    PushTarget Target,
    string? PayloadJson = null,
    string? DeduplicationKey = null,
    string? CorrelationKey = null,
    DateTimeOffset? ScheduledAtUtc = null,
    int MaxRetries = PushEvent.DefaultMaxRetries)
{
    public const int DefaultMaxRetries = 5;
    public const int MaxEventCategoryLength = 80;
    public const int MaxTargetJsonLength = 2048;
    public const int MaxPayloadJsonLength = 16 * 1024;
    public const int MaxKeyLength = 200;

    public string EventCategory => Category.Value;

    public string TargetType => Target.KindValue;

    public int? TargetUserId => Target.TryGetSingleIntValue(PushTargetKind.User);

    public string TargetJson => Target.ToJson();

    public static PushEvent ForUser(
        int userId,
        PushEventCategory category,
        string? payloadJson = null,
        string? deduplicationKey = null,
        string? correlationKey = null)
        => new(
            category,
            PushTarget.ForUser(userId),
            payloadJson,
            deduplicationKey,
            correlationKey);

    public static PushEvent ForBroadcast(
        PushEventCategory category,
        string? payloadJson = null,
        string? deduplicationKey = null,
        string? correlationKey = null)
        => new(
            category,
            PushTarget.Broadcast(),
            payloadJson,
            deduplicationKey,
            correlationKey);

    public static PushEvent ForAuthenticatedUsers(
        PushEventCategory category,
        string? payloadJson = null,
        string? deduplicationKey = null,
        string? correlationKey = null)
        => new(
            category,
            PushTarget.AuthenticatedUsers(),
            payloadJson,
            deduplicationKey,
            correlationKey);

    public PushEvent Normalize()
    {
        var category = Category.Normalize();
        var target = Target.Normalize();
        var targetJson = target.ToJson();
        if (targetJson.Length > MaxTargetJsonLength)
        {
            throw new ArgumentException(
                $"Push event target JSON cannot exceed {MaxTargetJsonLength} characters.",
                nameof(Target));
        }

        var scheduledAtUtc = ScheduledAtUtc?.ToUniversalTime();
        if (MaxRetries is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetries),
                "Push event max retries must be between 0 and 20.");
        }

        return this with
        {
            Category = category,
            Target = target,
            PayloadJson = NormalizePayloadJson(PayloadJson),
            DeduplicationKey = NormalizeOptionalKey(DeduplicationKey, nameof(DeduplicationKey)),
            CorrelationKey = NormalizeOptionalKey(CorrelationKey, nameof(CorrelationKey)),
            ScheduledAtUtc = scheduledAtUtc,
            MaxRetries = MaxRetries
        };
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

    private static string? NormalizeOptionalKey(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > MaxKeyLength)
        {
            throw new ArgumentException(
                $"Push event key cannot exceed {MaxKeyLength} characters.",
                parameterName);
        }

        return normalized;
    }
}

public readonly record struct PushEventCategory(string Value)
{
    public static PushEventCategory TopBarNotificationStateChanged { get; } = new("topbar.notification-state-changed");

    public static PushEventCategory TopBarMessageStateChanged { get; } = new("topbar.message-state-changed");

    public static PushEventCategory TopBarBannerStateChanged { get; } = new("topbar.banner-state-changed");

    public static PushEventCategory ModuleStateChanged { get; } = new("module.state-changed");

    public static PushEventCategory ModuleSpecific { get; } = new("module.specific");

    public PushEventCategory Normalize()
    {
        var normalized = Value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Push event category is required.", nameof(Value));
        }

        if (normalized.Length > PushEvent.MaxEventCategoryLength)
        {
            throw new ArgumentException(
                $"Push event category cannot exceed {PushEvent.MaxEventCategoryLength} characters.",
                nameof(Value));
        }

        return new PushEventCategory(normalized);
    }

    public override string ToString()
        => Value;
}

public sealed record PushTarget(PushTargetKind Kind, IReadOnlyList<string> Values)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string KindValue => Kind.ToJsonValue();

    public static PushTarget ForUser(int userId)
        => new(PushTargetKind.User, [NormalizePositiveId(userId, nameof(userId))]);

    public static PushTarget ForRole(int roleId)
        => new(PushTargetKind.Role, [NormalizePositiveId(roleId, nameof(roleId))]);

    public static PushTarget Broadcast()
        => new(PushTargetKind.Broadcast, []);

    public static PushTarget AuthenticatedUsers()
        => new(PushTargetKind.Authenticated, []);

    public static PushTarget ForApp(string appKey)
        => new(PushTargetKind.App, [appKey]);

    public static PushTarget ForModule(string moduleKey)
        => new(PushTargetKind.Module, [moduleKey]);

    public PushTarget Normalize()
    {
        var normalizedValues = Kind switch
        {
            PushTargetKind.User or PushTargetKind.Role => NormalizeNumericValues(Values, Kind),
            PushTargetKind.App or PushTargetKind.Module => NormalizeKeyValues(Values, Kind),
            PushTargetKind.Broadcast or PushTargetKind.Authenticated => [],
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), "Unsupported push target kind.")
        };

        return new PushTarget(Kind, normalizedValues);
    }

    public string ToJson()
    {
        var normalized = Normalize();
        var json = JsonSerializer.Serialize(
            new PushTargetJson(normalized.KindValue, normalized.Values),
            JsonOptions);

        if (json.Length > PushEvent.MaxTargetJsonLength)
        {
            throw new ArgumentException(
                $"Push event target JSON cannot exceed {PushEvent.MaxTargetJsonLength} characters.",
                nameof(Values));
        }

        return json;
    }

    public int? TryGetSingleIntValue(PushTargetKind expectedKind)
    {
        var normalized = Normalize();
        if (normalized.Kind != expectedKind || normalized.Values.Count != 1)
        {
            return null;
        }

        return int.TryParse(
            normalized.Values[0],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static string NormalizePositiveId(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Push target ids must be greater than zero.");
        }

        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] NormalizeNumericValues(IReadOnlyList<string>? values, PushTargetKind kind)
    {
        if (values is null || values.Count == 0)
        {
            throw new ArgumentException($"Push target kind '{kind.ToJsonValue()}' requires at least one id.", nameof(Values));
        }

        var normalized = values
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value =>
            {
                if (!int.TryParse(
                        value,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var id)
                    || id <= 0)
                {
                    throw new ArgumentException($"Push target kind '{kind.ToJsonValue()}' requires positive integer ids.", nameof(Values));
                }

                return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new ArgumentException($"Push target kind '{kind.ToJsonValue()}' requires at least one id.", nameof(Values));
        }

        return normalized;
    }

    private static string[] NormalizeKeyValues(IReadOnlyList<string>? values, PushTargetKind kind)
    {
        if (values is null || values.Count == 0)
        {
            throw new ArgumentException($"Push target kind '{kind.ToJsonValue()}' requires at least one key.", nameof(Values));
        }

        var normalized = values
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new ArgumentException($"Push target kind '{kind.ToJsonValue()}' requires at least one key.", nameof(Values));
        }

        return normalized;
    }

    private sealed record PushTargetJson(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("ids")] IReadOnlyList<string> Ids);
}

public enum PushTargetKind
{
    User,
    Role,
    Broadcast,
    Authenticated,
    App,
    Module
}

public static class PushTargetKindExtensions
{
    public static string ToJsonValue(this PushTargetKind kind)
        => kind switch
        {
            PushTargetKind.User => "user",
            PushTargetKind.Role => "role",
            PushTargetKind.Broadcast => "broadcast",
            PushTargetKind.Authenticated => "authenticated",
            PushTargetKind.App => "app",
            PushTargetKind.Module => "module",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported push target kind.")
        };
}
