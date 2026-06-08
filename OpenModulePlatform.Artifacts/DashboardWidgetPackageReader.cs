using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenModulePlatform.Artifacts;

public sealed record PortableDashboardWidgetPackage(
    string Format,
    int FormatVersion,
    string PackageVersion,
    string? ModuleKey,
    string? Author,
    IReadOnlyList<PortableDashboardWidgetDefinition> Widgets);

public sealed record PortableDashboardWidgetDefinition(
    string WidgetKey,
    string WidgetVersion,
    string Title,
    string? Description,
    string WidgetType,
    string? Payload,
    string? ModuleKey,
    string? Author,
    IReadOnlyList<string> PermissionNames,
    IReadOnlyList<string> RoleNames);

/// <summary>
/// Reads portable Portal dashboard widget definition documents.
/// </summary>
public sealed class DashboardWidgetPackageReader
{
    public const string FormatName = "omp.portal.dashboard.widgets";

    private const int FormatVersion = 1;
    private const string LegacyWidgetVersion = "0.0.0";
    private const int MaxJsonBytes = 1024 * 1024;
    private const int MaxPayloadLength = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public async Task<PortableDashboardWidgetPackage> ReadAsync(
        Stream stream,
        string sourceName,
        CancellationToken ct)
    {
        if (!stream.CanRead)
        {
            throw new InvalidOperationException("The dashboard widget JSON stream is not readable.");
        }

        var json = await ReadJsonWithSizeLimitAsync(stream, ct);
        var document = JsonSerializer.Deserialize<DashboardWidgetDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("The dashboard widget JSON file is empty.");
        ValidateDocument(document, sourceName);

        return new PortableDashboardWidgetPackage(
            document.Format,
            document.FormatVersion,
            CleanVersionText(document.PackageVersion, "packageVersion") ?? LegacyWidgetVersion,
            CleanOptionalKey(document.ModuleKey, "moduleKey", 100),
            CleanOptionalText(document.Author, "author", 200),
            document.Widgets.Select(item => Normalize(document, item)).ToArray());
    }

    private static void ValidateDocument(DashboardWidgetDocument document, string sourceName)
    {
        if (!string.Equals(document.Format, FormatName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Dashboard widget JSON '{sourceName}' must contain format '{FormatName}'.");
        }

        if (document.FormatVersion != FormatVersion)
        {
            throw new InvalidOperationException($"Dashboard widget JSON formatVersion must be {FormatVersion}.");
        }

        if (document.Widgets.Count == 0)
        {
            throw new InvalidOperationException("Dashboard widget JSON must contain at least one widget.");
        }
    }

    private static async Task<string> ReadJsonWithSizeLimitAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var scratch = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(scratch.AsMemory(0, scratch.Length), ct);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxJsonBytes)
            {
                throw new InvalidOperationException($"The dashboard widget JSON exceeds the limit of {MaxJsonBytes} bytes.");
            }

            buffer.Write(scratch, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    private static PortableDashboardWidgetDefinition Normalize(
        DashboardWidgetDocument document,
        DashboardWidgetDocumentItem item)
    {
        var widgetKey = CleanRequiredKey(item.WidgetKey, "widgetKey", 200);
        var widgetVersion = CleanVersionText(item.WidgetVersion ?? document.PackageVersion, "widgetVersion") ?? LegacyWidgetVersion;
        var title = CleanRequiredText(item.Title, "title", 200);
        var description = CleanOptionalText(item.Description, "description", 1000);
        var widgetType = CleanRequiredKey(item.WidgetType, "widgetType", 50);
        var moduleKey = CleanOptionalKey(item.ModuleKey ?? document.ModuleKey, "moduleKey", 100);
        var payload = CleanOptionalText(item.Payload, "payload", MaxPayloadLength);
        var author = CleanOptionalText(item.Author ?? document.Author, "author", 200);
        if (item.PermissionNames is null || item.RoleNames is null)
        {
            throw new InvalidOperationException(
                "Each dashboard widget must contain permissionNames and roleNames arrays. Use empty arrays for unrestricted widgets.");
        }

        return new PortableDashboardWidgetDefinition(
            widgetKey,
            widgetVersion,
            title,
            description,
            widgetType,
            payload,
            moduleKey,
            author,
            CleanDistinctNames(item.PermissionNames, "permissionNames"),
            CleanDistinctNames(item.RoleNames, "roleNames"));
    }

    private static string CleanRequiredText(string? value, string propertyName, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Each dashboard widget must contain {propertyName}.");
        }

        if (text.Length > maxLength)
        {
            throw new InvalidOperationException($"Dashboard widget {propertyName} must be at most {maxLength} characters.");
        }

        return text;
    }

    private static string CleanRequiredKey(string? value, string propertyName, int maxLength)
    {
        var key = CleanRequiredText(value, propertyName, maxLength);
        if (!key.All(IsPortableKeyCharacter))
        {
            throw new InvalidOperationException(
                $"Dashboard widget {propertyName} may only contain letters, digits, period, underscore, colon, or hyphen.");
        }

        return key;
    }

    private static string? CleanOptionalText(string? value, string propertyName, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Length > maxLength)
        {
            throw new InvalidOperationException($"Dashboard widget {propertyName} must be at most {maxLength} characters.");
        }

        return text;
    }

    private static string? CleanVersionText(string? value, string propertyName)
    {
        var version = CleanOptionalText(value, propertyName, 50);
        if (version is null)
        {
            return null;
        }

        if (!version.All(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '+' or '-'))
        {
            throw new InvalidOperationException(
                $"Dashboard widget {propertyName} may only contain letters, digits, period, underscore, plus, or hyphen.");
        }

        return version;
    }

    private static IReadOnlyList<string> CleanDistinctNames(IReadOnlyList<string>? values, string propertyName)
    {
        if (values is null)
        {
            return [];
        }

        return values
            .Select(value => CleanOptionalText(value, propertyName, 200))
            .Where(text => text is not null)
            .Select(text => text!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? CleanOptionalKey(string? value, string propertyName, int maxLength)
    {
        var key = CleanOptionalText(value, propertyName, maxLength);
        if (key is null)
        {
            return null;
        }

        if (!key.All(IsPortableKeyCharacter))
        {
            throw new InvalidOperationException(
                $"Dashboard widget {propertyName} may only contain letters, digits, period, underscore, colon, or hyphen.");
        }

        return key;
    }

    private static bool IsPortableKeyCharacter(char ch)
        => char.IsLetterOrDigit(ch) || ch is '.' or '_' or ':' or '-';

    private sealed class DashboardWidgetDocument
    {
        public string Format { get; set; } = string.Empty;

        public int FormatVersion { get; set; }

        public string? PackageVersion { get; set; }

        public string? ModuleKey { get; set; }

        public string? Author { get; set; }

        public List<DashboardWidgetDocumentItem> Widgets { get; set; } = [];
    }

    private sealed class DashboardWidgetDocumentItem
    {
        public string WidgetKey { get; set; } = string.Empty;

        public string? WidgetVersion { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string WidgetType { get; set; } = "portal";

        public string? Payload { get; set; }

        public string? ModuleKey { get; set; }

        public string? Author { get; set; }

        public List<string>? PermissionNames { get; set; }

        public List<string>? RoleNames { get; set; }
    }
}
