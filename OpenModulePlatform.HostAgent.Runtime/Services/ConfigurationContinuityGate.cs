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
    /// The tolerance of the ASP.NET Core JSON configuration provider: an
    /// appsettings.json with comments or trailing commas runs, and operators edit the
    /// deployed file by hand, so the gate must read it the same way.
    /// </summary>
    private static readonly JsonDocumentOptions AppSettingsDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

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
    /// there is no readable previous file, or when either side is not a JSON object;
    /// <see cref="EvaluateViolation"/> then stops a deployment over an unreadable
    /// previous file.
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
            next = ParseAppSettings(effectiveFiles[newFileIndex].FileContent) as JsonObject;
            if (ParseAppSettings(File.ReadAllText(previousPath)) is not JsonObject previous || next is null)
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
    /// "OmpAuth:Oidc"), or null when the deployment may proceed. An absent previous
    /// file (first deploy), and a new resolution whose rendered content is not valid
    /// JSON (other validation reports that), both yield null. A previous file that
    /// exists but cannot be read, or is not a JSON object, fails closed: it is no
    /// evidence that the host had nothing, so the deployment must not overwrite it.
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
            previous = JsonDocument.Parse(WithoutByteOrderMark(File.ReadAllText(previousPath)), AppSettingsDocumentOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UnreadablePreviousFileViolation(previousPath, "could not be read");
        }
        catch (JsonException)
        {
            // Never include the exception message or the content: the file may
            // hold secrets.
            return UnreadablePreviousFileViolation(previousPath, "is not valid JSON (comments and trailing commas are accepted)");
        }

        using (previous)
        {
            if (previous.RootElement.ValueKind != JsonValueKind.Object)
            {
                return UnreadablePreviousFileViolation(previousPath, "is not valid JSON configuration (the root must be a JSON object)");
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
                next = JsonDocument.Parse(
                    WithoutByteOrderMark(ArtifactConfigurationFileWriter.Render(newFile.FileContent, variables)),
                    AppSettingsDocumentOptions);
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

    private static JsonNode? ParseAppSettings(string content)
        => JsonNode.Parse(WithoutByteOrderMark(content), documentOptions: AppSettingsDocumentOptions);

    // File.ReadAllText already drops a UTF-8 BOM; configuration content from other
    // sources may still start with one, which the JSON reader rejects on a string.
    private static string WithoutByteOrderMark(string content)
        => content.StartsWith('\uFEFF') ? content[1..] : content;

    private static string UnreadablePreviousFileViolation(string previousPath, string reason)
        => "the deployed configuration file " + previousPath + " " + reason +
           ", so it is unknown what configuration this host runs with; refusing to overwrite it with the new artifact resolution. " +
           "The currently deployed version keeps running. To continue, do one of: " +
           "(1) correct " + previousPath + " so HostAgent can read it as a JSON object, then retry; " +
           "(2) if the artifact configuration or a config overlay provides every setting this app instance " +
           "needs, delete " + previousPath + " on this host and retry the deployment";
}
