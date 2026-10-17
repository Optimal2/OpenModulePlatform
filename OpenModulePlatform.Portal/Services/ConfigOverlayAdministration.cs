using System.Text.Json;
using System.Text.Json.Nodes;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Models;

namespace OpenModulePlatform.Portal.Services;

// All operations use the connected installation database. No client-supplied connection or root.
public interface IConfigOverlayAdminStore
{
    Task<OverlayAdminSnapshot> ReadAsync(CancellationToken ct);
    Task<OverlayAdminDetail?> DetailAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<OverlaySourceRow>> SourcesAsync(int artifactId, CancellationToken ct);
    Task<int> SaveAsync(PortableConfigOverlayDocument document, int? id, string? expectedHash, CancellationToken ct);
    Task ChangeStateAsync(int id, string expectedHash, bool? enabled, CancellationToken ct);
}

public sealed record OverlayAdminSnapshot(bool SchemaReady, IReadOnlyList<HostRow> Hosts,
    IReadOnlyList<ArtifactRow> Artifacts, IReadOnlyList<ConfigOverlayDocumentRow> Overlays, bool HasHostScope = true);
public sealed record OverlayAdminDetail(ConfigOverlayDocumentRow Summary, string Json, IReadOnlyList<OverlaySourceRow> Files);
// Content stays inside the service; effective-source responses contain metadata only.
public sealed record OverlaySourceRow(int RowId, int? DocumentId, string RelativePath, string Content,
    string? MergeMode, bool IsEnabled, DateTime UpdatedUtc);
public sealed record OverlayEffectiveReadback(IReadOnlyList<EffectiveConfigurationFileDto> Files,
    IReadOnlyList<string> Diagnostics);

public sealed class ConfigOverlayAdministration(IConfigOverlayAdminStore store)
{
    public static PortableConfigOverlayDocument ValidateDraft(string json, string scope, string? hostKey,
        OverlayAdminSnapshot snapshot, ConfigOverlayDocumentRow? existing)
    {
        if (!snapshot.SchemaReady)
            throw new InvalidOperationException("Upgrade the platform schema before editing overlays.");
        if (json.Length > 1024 * 1024)
            throw new InvalidOperationException("The overlay document exceeds the editor size limit.");
        var parsedScope = ConfigOverlayHostScopeNames.Parse(scope);
        if (parsedScope == ConfigOverlayHostScope.Host
            && snapshot.Hosts.Count(h => string.Equals(h.HostKey, hostKey, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidOperationException("Select an unambiguous registered host key in this installation.");
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("Enter a JSON object.");
        if (root["configurationFiles"] is not null && root["configurationFiles"] is not JsonArray)
            throw new InvalidOperationException("Configuration files must be an array.");
        if (root["configurationFiles"] is JsonArray entries)
            foreach (var entry in entries)
            {
                var path = entry?["relativePath"]?.GetValue<string>()?.Trim();
                if (path is not null && (Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\')))
                    throw new InvalidOperationException("Configuration paths must be relative to the deployment root.");
            }
        root["hostScope"] = ConfigOverlayHostScopeNames.ToName(parsedScope);
        root["hostKey"] = parsedScope == ConfigOverlayHostScope.AllHosts ? "*" : hostKey;
        var document = ConfigOverlayPackageReader.ReadInlineConfigOverlay(root.ToJsonString());
        if (existing is not null && (document.OverlayKey != existing.OverlayKey
            || document.OverlayVersion != existing.OverlayVersion || document.HostKey != existing.HostKey
            || ConfigOverlayHostScopeNames.ToName(document.HostScope) != existing.HostScope))
            throw new InvalidOperationException("Identity and scope cannot change during edit. Create a new overlay instead.");
        foreach (var file in document.ConfigurationFiles)
        {
            if (Mode(file.MergeMode, file.RelativePath) == "merge"
                && JsonNode.Parse(file.FileContent) is not JsonObject)
                throw new InvalidOperationException("Merge content must be a JSON object.");
        }
        return document;
    }

    public async Task<OverlayEffectiveReadback> ReadEffectiveAsync(OverlayAdminSnapshot snapshot,
        string hostKey, int artifactId, CancellationToken ct)
    {
        var host = snapshot.Hosts.SingleOrDefault(h => string.Equals(h.HostKey, hostKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Select a registered host in this installation.");
        var artifact = snapshot.Artifacts.SingleOrDefault(a => a.ArtifactId == artifactId)
            ?? throw new InvalidOperationException("Select an artifact in this installation.");
        var candidates = new List<Candidate>();
        var diagnostics = new List<string>();
        if (!host.IsEnabled) diagnostics.Add("Selected host is disabled; this describes saved configuration only.");
        if (!artifact.IsEnabled) diagnostics.Add("Selected artifact is disabled; this describes saved configuration only.");
        if (!snapshot.SchemaReady) diagnostics.Add("Schema not ready: writes are disabled; upgrade the platform schema.");
        if (!snapshot.HasHostScope) diagnostics.Add("HostScope is missing: exact-host fallback only.");
        foreach (var file in await store.SourcesAsync(artifactId, ct))
        {
            if (file.IsEnabled) candidates.Add(new(file, null));
            else diagnostics.Add($"Artifact file {file.RelativePath}: disabled.");
        }
        foreach (var overlay in snapshot.Overlays)
        {
            var allHosts = overlay.HostScope == "AllHosts";
            if (!(allHosts && snapshot.HasHostScope) && !Equal(overlay.HostKey, hostKey)) continue;
            if (!Matches(overlay.ModuleKey, artifact.ModuleKey) || !Matches(overlay.AppKey, artifact.AppKey)
                || !Matches(overlay.PackageType, artifact.PackageType) || !Matches(overlay.TargetName, artifact.TargetName)) continue;
            if (!overlay.IsEnabled) { diagnostics.Add($"{overlay.OverlayKey} {overlay.OverlayVersion}: disabled."); continue; }
            if (overlay.ArtifactVersion is not null && ArtifactVersionComparer.Compare(artifact.Version, overlay.ArtifactVersion) < 0)
            {
                diagnostics.Add($"{overlay.OverlayKey} {overlay.OverlayVersion}: excluded by artifact minimum {overlay.ArtifactVersion}.");
                continue;
            }
            var detail = await store.DetailAsync(overlay.ConfigOverlayDocumentId, ct);
            if (detail is null) { diagnostics.Add($"{overlay.OverlayKey}: missing document; reload."); continue; }
            foreach (var file in detail.Files)
            {
                if (file.IsEnabled) candidates.Add(new(file, overlay));
                else diagnostics.Add($"{overlay.OverlayKey} / {file.RelativePath}: disabled file.");
            }
            if (detail.Files.Count == 0) diagnostics.Add($"{overlay.OverlayKey}: no configuration files.");
        }
        return Resolve(candidates, diagnostics);
    }

    public sealed record Candidate(OverlaySourceRow File, ConfigOverlayDocumentRow? Overlay)
    {
        public int Priority => Overlay is null ? 0 : Overlay.HostScope == "AllHosts" ? 1 : 2;
    }

    public static OverlayEffectiveReadback Resolve(IEnumerable<Candidate> candidates, List<string>? diagnostics = null)
    {
        diagnostics ??= [];
        var results = new List<EffectiveConfigurationFileDto>();
        foreach (var group in candidates.GroupBy(c => c.File.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var layers = group.GroupBy(c => c.Priority).Select(g => g.Aggregate((a, b) => Wins(b, a) ? b : a))
                .OrderBy(c => c.Priority).ToList();
            var winner = layers[^1];
            var contributing = new HashSet<Candidate>();
            string? content = null;
            var valid = true;
            foreach (var layer in layers)
            {
                if (layer.Priority == 0 || Mode(layer.File.MergeMode, group.Key) == "replace")
                {
                    content = layer.File.Content;
                    contributing.Clear();
                }
                else if (content is null) content = layer.File.Content;
                else
                {
                    try { content = Merge(content, layer.File.Content); }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                    {
                        diagnostics.Add($"{group.Key}: merge failed; both inputs must be JSON objects. No effective result.");
                        valid = false;
                        break;
                    }
                }
                contributing.Add(layer);
            }
            if (!valid) continue;
            EffectiveConfigurationSourceDto Dto(Candidate c) => new(c.File.RelativePath, c.Priority,
                c.Overlay is null ? "Artifact" : "ConfigOverlay", c.Overlay?.OverlayKey, c.Overlay?.OverlayVersion,
                c.Overlay?.HostScope, c.File.MergeMode, Mode(c.File.MergeMode, c.File.RelativePath),
                ReferenceEquals(c, winner), !contributing.Contains(c));
            results.Add(new(group.Key, Dto(winner), group.OrderBy(c => c.Priority).Select(Dto).ToArray()));
        }
        if (results.Count == 0) diagnostics.Add("No effective configuration files found.");
        return new(results.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), diagnostics);
    }

    private static bool Wins(Candidate a, Candidate b)
    {
        var version = ArtifactVersionComparer.Compare(a.Overlay?.OverlayVersion, b.Overlay?.OverlayVersion);
        var aUpdated = a.Overlay?.UpdatedUtc ?? a.File.UpdatedUtc;
        var bUpdated = b.Overlay?.UpdatedUtc ?? b.File.UpdatedUtc;
        return version != 0 ? version > 0 : aUpdated != bUpdated
            ? aUpdated > bUpdated : a.File.RowId > b.File.RowId;
    }
    public static string Mode(string? mode, string path) => string.IsNullOrWhiteSpace(mode)
        ? path.Trim().EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "merge" : "replace"
        : string.Equals(mode.Trim(), "merge", StringComparison.OrdinalIgnoreCase) ? "merge" : "replace";
    private static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Matches(string? selector, string? value) => selector is null || Equal(selector, value);
    private static string Merge(string lower, string upper)
    {
        var target = JsonNode.Parse(lower) as JsonObject ?? throw new InvalidOperationException();
        var source = JsonNode.Parse(upper) as JsonObject ?? throw new InvalidOperationException();
        MergeObjects(target, source);
        return target.ToJsonString();
    }
    private static void MergeObjects(JsonObject target, JsonObject source)
    {
        foreach (var pair in source)
            if (target[pair.Key] is JsonObject child && pair.Value is JsonObject value) MergeObjects(child, value);
            else target[pair.Key] = pair.Value?.DeepClone();
    }
}
