using System.Text.Json;
using System.Text.Json.Nodes;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Checks that the resolved <c>appsettings.json</c> carries every required root
/// section. Callers pass what <see cref="OmpHostArtifactRepository.GetArtifactConfigurationFilesAsync"/>
/// resolved -- and, for web apps, what the built-in configuration was merged
/// with -- which is the content HostAgent writes: a merge-mode overlay (MergeMode
/// NULL on a .json path, or 'merge') is already deep-merged onto the artifact's
/// configuration row there, and a 'replace' overlay stands alone. The verdict is
/// therefore about the final file, never about the overlay by itself.
/// </summary>
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
        return $"Incomplete configuration — the resolved appsettings.json is missing required sections: {string.Join(", ", missingSections)}. " +
            "The resolved file is the artifact configuration with any merge-mode overlay merged onto it, or a replace-mode overlay alone. " +
            "Add the sections to the artifact configuration or to a config overlay; a merge-mode overlay only needs the missing sections.";
    }
}
