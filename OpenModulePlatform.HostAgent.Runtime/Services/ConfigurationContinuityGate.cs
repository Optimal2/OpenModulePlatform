using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// Top-level appsettings.json keys the host owns rather than the module. A
    /// module may drop them from its packaged configuration on purpose. Host-owned
    /// means the value on the host holds: when the new resolution leaves such a key
    /// out, <see cref="CarryOverHostOwnedKeys"/> copies the previously deployed
    /// value into the new file, so an operator's restrictive AllowedHosts is never
    /// removed silently. When the artifact or an overlay sets the key, the new
    /// resolution wins. Because the value is carried rather than lost, the gate
    /// never fails on these keys - neither the key itself nor anything below it.
    /// Only exact top-level names match (case-insensitively); a module section such
    /// as "LoggingSettings", or a nested key with the same name, is module
    /// configuration. Documented in docs/VERSIONING_AND_IDENTITIES.md.
    /// </summary>
    internal static readonly IReadOnlySet<string> HostOwnedTopLevelKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AllowedHosts", "Logging" };

    /// <summary>
    /// Returns the effective files with every host-owned top-level key that the
    /// previously deployed appsettings.json has, and the new appsettings.json
    /// resolution does not set (compared case-insensitively), copied over from the
    /// previous file. <paramref name="carriedKeys"/> names the keys that were
    /// carried. The files are returned unchanged when nothing is carried, when
    /// there is no readable previous file, or when either side is not a JSON object.
    /// </summary>
    public static IReadOnlyList<ArtifactConfigurationFileDescriptor> CarryOverHostOwnedKeys(
        string targetPath,
        IReadOnlyList<ArtifactConfigurationFileDescriptor> effectiveFiles,
        out IReadOnlyList<string> carriedKeys)
    {
        carriedKeys = [];

        var newFileIndex = -1;
        for (var i = 0; i < effectiveFiles.Count; i++)
        {
            if (string.Equals(effectiveFiles[i].RelativePath, AppSettingsRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                newFileIndex = i;
                break;
            }
        }

        var previousPath = Path.Join(targetPath, AppSettingsRelativePath);
        if (newFileIndex < 0 || !File.Exists(previousPath))
        {
            return effectiveFiles;
        }

        JsonObject? next;
        var carried = new List<string>();
        try
        {
            next = JsonNode.Parse(effectiveFiles[newFileIndex].FileContent) as JsonObject;
            if (JsonNode.Parse(File.ReadAllText(previousPath)) is not JsonObject previous || next is null)
            {
                return effectiveFiles;
            }

            var nextKeys = next.Select(property => property.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var property in previous)
            {
                if (HostOwnedTopLevelKeys.Contains(property.Key) && nextKeys.Add(property.Key))
                {
                    next[property.Key] = property.Value?.DeepClone();
                    carried.Add(property.Key);
                }
            }
        }
        // ArgumentException: JsonObject rejects duplicate property names when it is
        // first enumerated. No reliable evidence either way: carry nothing.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return effectiveFiles;
        }

        if (carried.Count == 0)
        {
            return effectiveFiles;
        }

        carriedKeys = carried;
        var newFile = effectiveFiles[newFileIndex];
        var result = effectiveFiles.ToArray();
        result[newFileIndex] = new ArtifactConfigurationFileDescriptor
        {
            ArtifactConfigurationFileId = newFile.ArtifactConfigurationFileId,
            ArtifactId = newFile.ArtifactId,
            RelativePath = newFile.RelativePath,
            FileContent = next.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
        };
        return result;
    }

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
                foreach (var section in previous.RootElement.EnumerateObject()
                             .Where(section => !HostOwnedTopLevelKeys.Contains(section.Name)))
                {
                    if (!next.RootElement.TryGetProperty(section.Name, out var nextSection))
                    {
                        missing.Add(section.Name);
                        continue;
                    }

                    if (section.Value.ValueKind == JsonValueKind.Object
                        && nextSection.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var child in section.Value.EnumerateObject()
                                     .Where(child => !nextSection.TryGetProperty(child.Name, out _)))
                        {
                            missing.Add(section.Name + ":" + child.Name);
                        }
                    }
                }

                if (missing.Count == 0)
                {
                    return null;
                }

                // The gate cannot tell an operator's lost section from a package that
                // dropped one on purpose, so the way past it is operator-controlled and
                // explicit: either provide the settings again or edit the previously
                // deployed file (the evidence the gate reads) and deploy again
                // (independent review, 2026-09-05).
                var previousFile = Path.Join(targetPath, AppSettingsRelativePath);
                return
                    "the previous deploy had configuration the new artifact resolution no longer provides: " +
                    string.Join(", ", missing) +
                    "; refusing to silently fall back to the built-in default configuration. " +
                    "The currently deployed version keeps running. To continue, do one of: " +
                    "(1) if the app still needs those settings, add them to a config overlay (or the artifact " +
                    "configuration file) for this app instance so the new resolution provides them, then retry; " +
                    "(2) if the removal is intended, delete those sections from " + previousFile +
                    " on this host and retry the deployment";
            }
        }
    }
}
