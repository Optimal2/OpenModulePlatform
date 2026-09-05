using System.Text.Json;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Continuity gate for web app deployments: when the previously deployed
/// appsettings.json on disk carries configuration the new artifact resolution no
/// longer provides, the deployment must fail loudly instead of silently falling
/// back to the built-in default.
///
/// The comparison is disk-based on purpose: HostAppDeploymentStates is
/// overwritten by failed attempts, so it cannot reliably carry "the last
/// SUCCESSFUL deploy". The file the previous deploy actually wrote is the only
/// durable evidence of what the instance was running with.
/// </summary>
internal static class ConfigurationContinuityGate
{
    private const string AppSettingsRelativePath = "appsettings.json";

    /// <summary>
    /// Returns a human-readable violation message naming every lost top-level
    /// section (and every lost second-level key below an object section, e.g.
    /// "OmpAuth:Oidc"), or null when the deployment may proceed. An unreadable
    /// or absent previous file, and a new resolution whose rendered content is
    /// not valid JSON (other validation reports that), both yield null.
    /// </summary>
    public static string? EvaluateViolation(
        string targetPath,
        IReadOnlyList<ArtifactConfigurationFileDescriptor> effectiveFiles,
        IReadOnlyDictionary<string, string> variables)
    {
        var previousPath = Path.Join(targetPath, AppSettingsRelativePath);
        if (!File.Exists(previousPath))
        {
            return null;
        }

        JsonDocument previous;
        try
        {
            previous = JsonDocument.Parse(File.ReadAllText(previousPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // No reliable evidence of what the previous deploy had: no gate.
            return null;
        }

        using (previous)
        {
            if (previous.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var newFile = effectiveFiles.FirstOrDefault(
                file => string.Equals(file.RelativePath, AppSettingsRelativePath, StringComparison.OrdinalIgnoreCase));
            if (newFile is null)
            {
                return
                    "the previous deploy wrote appsettings.json, but the new artifact resolution provides no " +
                    "appsettings.json; refusing to silently fall back to the built-in default configuration";
            }

            JsonDocument next;
            try
            {
                next = JsonDocument.Parse(ArtifactConfigurationFileWriter.Render(newFile.FileContent, variables));
            }
            catch (JsonException)
            {
                return null;
            }

            using (next)
            {
                if (next.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var missing = new List<string>();
                foreach (var section in previous.RootElement.EnumerateObject())
                {
                    if (!next.RootElement.TryGetProperty(section.Name, out var nextSection))
                    {
                        missing.Add(section.Name);
                        continue;
                    }

                    if (section.Value.ValueKind == JsonValueKind.Object
                        && nextSection.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var child in section.Value.EnumerateObject())
                        {
                            if (!nextSection.TryGetProperty(child.Name, out _))
                            {
                                missing.Add(section.Name + ":" + child.Name);
                            }
                        }
                    }
                }

                if (missing.Count == 0)
                {
                    return null;
                }

                // The gate cannot tell an operator's lost section from a package that
                // dropped one on purpose, so the way past it is operator-controlled and
                // explicit: edit the previously deployed file (the evidence the gate
                // reads) and deploy again (independent review, 2026-09-05).
                return
                    "the previous deploy had configuration the new artifact resolution no longer provides: " +
                    string.Join(", ", missing) +
                    "; refusing to silently fall back to the built-in default configuration. " +
                    "If the removal is intended, delete those sections from the previously deployed " +
                    Path.Join(targetPath, AppSettingsRelativePath) +
                    " and deploy again";
            }
        }
    }
}
