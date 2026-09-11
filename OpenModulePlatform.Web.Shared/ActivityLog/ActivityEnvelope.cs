// File: OpenModulePlatform.Web.Shared/ActivityLog/ActivityEnvelope.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenModulePlatform.Web.Shared.ActivityLog;

/// <summary>
/// The stored JSON shape of an activity entry. This is the contract between every
/// module's writer and the Portal's reader, so its rules are strict:
/// <list type="bullet">
/// <item><see cref="V"/> is the envelope version. Versions are additive: a new
/// version may add fields, never rename, remove or reinterpret one, so a reader
/// built for an older version still reads the newer entry and simply does not
/// see the additions. That is why the reader accepts every version from
/// <see cref="MinVersion"/> up, including versions newer than itself.</item>
/// <item><see cref="Event"/> and <see cref="Summary"/> are mandatory in every version,
/// so a list row can always be rendered even by a reader that does not know the
/// version.</item>
/// <item><see cref="Data"/> and <see cref="Args"/> are opaque to readers.</item>
/// </list>
/// Versions so far:
/// <list type="number">
/// <item>event, module, app, outcome, summary, subject, actor, correlation, data.</item>
/// <item>adds <see cref="MessageKey"/> and <see cref="Args"/>: the template the
/// summary was built from and the values that filled it, so a later reader can
/// render the line in the viewer's language. The writer emits version 2 only
/// when an entry carries a message key; everything else stays version 1.</item>
/// </list>
/// </summary>
public sealed class ActivityEnvelope
{
    /// <summary>The newest version this library writes.</summary>
    public const int CurrentVersion = 2;

    /// <summary>The oldest version the reader accepts.</summary>
    public const int MinVersion = 1;

    [JsonPropertyName("v")]
    public int V { get; set; } = MinVersion;

    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    /// <summary>Module key (omp.Modules.ModuleKey). Redundant with the schema the row lives in, but survives export.</summary>
    [JsonPropertyName("module")]
    public string? Module { get; set; }

    /// <summary>Component key of the app that wrote the entry (web app, worker).</summary>
    [JsonPropertyName("app")]
    public string? App { get; set; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public ActivitySubject? Subject { get; set; }

    [JsonPropertyName("actor")]
    public ActivityActor? Actor { get; set; }

    [JsonPropertyName("correlation")]
    public string? Correlation { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    /// <summary>
    /// Version 2. The key of the message template the summary was built from: the
    /// event key by convention, with a suffix for a variant of the same event
    /// (<c>universal_package.imported.failed</c>). Null in version 1 entries.
    /// </summary>
    [JsonPropertyName("messageKey")]
    public string? MessageKey { get; set; }

    /// <summary>Version 2. The values that filled the template, by name. Opaque to readers.</summary>
    [JsonPropertyName("args")]
    public JsonElement? Args { get; set; }
}

/// <summary>Serialization settings shared by writer and reader: camelCase, nulls omitted.</summary>
public static class ActivityLogJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize(ActivityEnvelope envelope)
        => JsonSerializer.Serialize(envelope, Options);

    /// <summary>
    /// Parses a stored entry. Every version from <see cref="ActivityEnvelope.MinVersion"/>
    /// up is accepted, newer ones included, because versions only add fields: the
    /// reader fills what it knows and ignores the rest. Malformed JSON, a version
    /// below the minimum, or a missing event or summary return null; callers then
    /// show the raw text.
    /// </summary>
    public static ActivityEnvelope? TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<ActivityEnvelope>(json, Options);
            if (envelope is null || envelope.V < ActivityEnvelope.MinVersion)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(envelope.Event) || string.IsNullOrWhiteSpace(envelope.Summary))
            {
                return null;
            }

            return envelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort extraction of the summary from any version, for a list row of an
    /// entry the current reader does not understand.
    /// </summary>
    public static string? TryReadSummary(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String
                ? summary.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
