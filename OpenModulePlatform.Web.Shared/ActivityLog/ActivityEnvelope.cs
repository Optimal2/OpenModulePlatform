// File: OpenModulePlatform.Web.Shared/ActivityLog/ActivityEnvelope.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenModulePlatform.Web.Shared.ActivityLog;

/// <summary>
/// The stored JSON shape of an activity entry. This is the contract between every
/// module's writer and the Portal's reader, so its rules are strict:
/// <list type="bullet">
/// <item><see cref="V"/> is the envelope version. Within a version fields may only
/// be added, never renamed, removed or given another meaning; anything else is a
/// new version with its own reader.</item>
/// <item><see cref="Event"/> and <see cref="Summary"/> are mandatory in every version,
/// so a list row can always be rendered even by a reader that does not know the
/// version.</item>
/// <item><see cref="Data"/> is opaque to readers.</item>
/// </list>
/// </summary>
public sealed class ActivityEnvelope
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")]
    public int V { get; set; } = CurrentVersion;

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
    /// Parses a stored entry into the version-1 envelope. Unknown versions and
    /// malformed JSON return null; callers then show the raw text. Missing optional
    /// fields are fine (additions are allowed within a version).
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
            if (envelope is null || envelope.V != ActivityEnvelope.CurrentVersion)
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
