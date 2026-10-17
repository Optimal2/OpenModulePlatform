using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Builds portable export JSON for a stored config overlay. The stored
/// document is canonical for overlays written through the current reader;
/// legacy rows may hold a metadata-only document while the normalized file
/// rows carry the actual content, so missing/empty configurationFiles are
/// reconstructed from those rows. Existing file entries are never replaced.
/// </summary>
public static class ConfigOverlayExportJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string Build(
        string storedJson,
        string hostScope,
        string hostKey,
        IReadOnlyList<PortableConfigOverlayConfigurationFile> files)
    {
        var root = JsonNode.Parse(storedJson) as JsonObject
            ?? throw new InvalidOperationException("Stored config overlay JSON must be a JSON object.");

        var scope = ConfigOverlayHostScopeNames.Parse(hostScope);
        root["hostScope"] = ConfigOverlayHostScopeNames.ToName(scope);
        root["hostKey"] = scope == ConfigOverlayHostScope.AllHosts
            ? ConfigOverlayHostScopeNames.AllHostsHostKey
            : hostKey;

        if (root["configurationFiles"] is not JsonArray existing || existing.Count == 0)
        {
            var array = new JsonArray();
            foreach (var file in files.OrderBy(static f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var item = new JsonObject
                {
                    ["relativePath"] = file.RelativePath,
                    ["fileContent"] = file.FileContent
                };
                if (!string.IsNullOrWhiteSpace(file.MergeMode))
                {
                    item["mergeMode"] = file.MergeMode;
                }

                array.Add(item);
            }

            root["configurationFiles"] = array;
        }

        return root.ToJsonString(JsonOptions);
    }
}
