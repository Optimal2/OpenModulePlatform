using System.Text.Json;
using System.Text.Json.Nodes;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

internal static class RequiredConfigSectionsValidator
{
    private const string AppSettingsRelativePath = "appsettings.json";

    public static string? Validate(
        IReadOnlyList<ArtifactConfigurationFileDescriptor> configurationFiles,
        IReadOnlyList<string> requiredRootSections)
    {
        var sections = requiredRootSections
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .ToList();

        if (sections.Count == 0)
        {
            return null;
        }

        var appSettingsFile = configurationFiles.FirstOrDefault(file =>
            string.Equals(file.RelativePath, AppSettingsRelativePath, StringComparison.OrdinalIgnoreCase));

        if (appSettingsFile is null)
        {
            return FormatWarning(sections);
        }

        JsonObject? rootObject;
        try
        {
            rootObject = JsonNode.Parse(appSettingsFile.FileContent) as JsonObject;
        }
        catch (JsonException)
        {
            return FormatWarning(sections);
        }

        if (rootObject is null)
        {
            return FormatWarning(sections);
        }

        var presentKeys = new HashSet<string>(
            rootObject.Select(property => property.Key),
            StringComparer.OrdinalIgnoreCase);

        var missing = sections
            .Where(section => !presentKeys.Contains(section))
            .ToList();

        if (missing.Count == 0)
        {
            return null;
        }

        return FormatWarning(missing);
    }

    private static string FormatWarning(IReadOnlyList<string> missingSections)
    {
        return $"Incomplete config overlay — missing required sections: {string.Join(", ", missingSections)}. " +
            "The overlay must include all required sections for the app to function correctly.";
    }
}
