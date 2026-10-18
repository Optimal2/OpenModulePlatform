using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    // Preview-bound mutation: the store re-derives the preview fingerprint
    // from the rows it locks in the same transaction as the mutation and
    // rejects when it no longer matches the submitted preview token.
    Task ChangeStateFromPreviewAsync(int id, string expectedHash, bool? enabled, string hostKey, int artifactId,
        string previewToken, CancellationToken ct);
}

public sealed record OverlayAdminSnapshot(bool SchemaReady, IReadOnlyList<HostRow> Hosts,
    IReadOnlyList<ArtifactRow> Artifacts, IReadOnlyList<ConfigOverlayDocumentRow> Overlays, bool HasHostScope = true);
public sealed record OverlayAdminDetail(ConfigOverlayDocumentRow Summary, string Json, IReadOnlyList<OverlaySourceRow> Files);
// Content stays inside the service; effective-source responses contain metadata only.
public sealed record OverlaySourceRow(int RowId, int? DocumentId, string RelativePath, string Content,
    string? MergeMode, bool IsEnabled, DateTime UpdatedUtc);
public sealed record OverlayEffectiveReadback(IReadOnlyList<EffectiveConfigurationFileDto> Files,
    IReadOnlyList<string> Diagnostics);
// Read-only pre-disable/pre-delete impact preview for one saved overlay on one
// host and artifact. Metadata only; file content is never included. Saved rows
// only: never live files, disk contents or final deployment configuration.
public sealed record OverlayRemovalFilePreview(
    string RelativePath,
    EffectiveConfigurationSourceDto? CurrentWinner,
    EffectiveConfigurationSourceDto? WinnerAfterRemoval,
    string Outcome,
    IReadOnlyList<string> Notes);
public sealed record OverlayRemovalPreview(
    int DocumentId,
    string OverlayKey,
    string OverlayVersion,
    string HostScope,
    string HostKey,
    bool DocumentWasEnabled,
    string TargetHostKey,
    int TargetArtifactId,
    string OverlaySha256,
    DateTime UpdatedUtc,
    bool DetailUnavailable,
    IReadOnlyList<OverlayRemovalFilePreview> Files,
    IReadOnlyList<string> Diagnostics,
    // SHA-256 over the selected document/host/artifact and every saved row
    // that can contribute to or fall back for the previewed paths. Hash only;
    // never file content. Per-action tokens derive from it.
    string PreviewFingerprint);

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
        var collected = await CollectCandidatesAsync(snapshot, hostKey, artifactId, ct);
        return Resolve(collected.Candidates, collected.Diagnostics);
    }

    // Action-bound token over a preview fingerprint: a token minted for
    // Disable cannot authorize Delete or Enable.
    public static string PreviewActionToken(string previewFingerprint, bool? enabled)
    {
        var action = enabled is null ? "Delete" : enabled.Value ? "Enable" : "Disable";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previewFingerprint + "|action=" + action)));
    }

    private static bool TokenEquals(string expected, string submitted) =>
        expected.Length == submitted.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(submitted));

    // Preview-bound mutation gate. Recomputes the preview from the caller's
    // fresh snapshot and fails closed on any mismatch, then delegates to the
    // store, which re-derives and compares the same fingerprint inside its
    // mutation transaction before writing.
    public async Task ChangeStateFromPreviewAsync(OverlayAdminSnapshot snapshot, int documentId,
        string hostKey, int artifactId, string expectedHash, bool? enabled, string previewToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(previewToken) || artifactId <= 0 || string.IsNullOrWhiteSpace(hostKey))
            throw new InvalidOperationException("A removal preview is required before this change. Reload and preview again.");
        if (enabled is true)
            throw new InvalidOperationException("A removal preview does not authorize enabling an overlay.");
        var preview = await PreviewRemovalAsync(snapshot, documentId, hostKey, artifactId, ct);
        if (preview.DetailUnavailable)
            throw new InvalidOperationException("Preview detail is unavailable. Reload and preview again.");
        if (!TokenEquals(PreviewActionToken(preview.PreviewFingerprint, enabled), previewToken))
            throw new InvalidOperationException("The preview is stale. Reload and preview again before changing.");
        await store.ChangeStateFromPreviewAsync(documentId, expectedHash, enabled, preview.TargetHostKey, artifactId, previewToken, ct);
    }

    // Removal preview excludes one saved document and compares, per owned
    // file path, both the winner and the merged effective content before and
    // after. It never enables, disables or deletes anything. Outcome is one
    // of "Unchanged", "ChangesEffectiveContent" (same winner, different
    // merged content), "FallsBack", "NoEffectiveFile" or "Unknown". The
    // returned PreviewFingerprint binds the selected document/host/artifact
    // and every contributing or fallback row; preview-bound Disable/Delete
    // re-derive and compare it at mutation time.
    public async Task<OverlayRemovalPreview> PreviewRemovalAsync(OverlayAdminSnapshot snapshot,
        int documentId, string hostKey, int artifactId, CancellationToken ct)
    {
        var selected = snapshot.Overlays.SingleOrDefault(o => o.ConfigOverlayDocumentId == documentId)
            ?? throw new InvalidOperationException("Select a saved overlay in this installation.");
        var collected = await CollectCandidatesAsync(snapshot, hostKey, artifactId, ct);
        var candidates = collected.Candidates; var diagnostics = collected.Diagnostics;
        var host = collected.Host; var artifact = collected.Artifact;
        diagnostics.Add("Preview covers only the selected host and artifact. Other hosts or artifacts using this document are not enumerated.");
        diagnostics.Add("Saved rows only: never live files, disk contents or final deployment configuration.");
        var currentDiagnostics = new List<string>();
        var current = ResolveWithContent(candidates, currentDiagnostics);
        var afterDiagnostics = new List<string>();
        var after = ResolveWithContent(candidates.Where(c => c.Overlay?.ConfigOverlayDocumentId != documentId), afterDiagnostics);
        foreach (var message in currentDiagnostics.Concat(afterDiagnostics))
            if (!diagnostics.Contains(message)) diagnostics.Add(message);
        var selectedDetail = await store.DetailAsync(documentId, ct);
        var files = new List<OverlayRemovalFilePreview>();
        if (selectedDetail is null)
        {
            diagnostics.Add($"{selected.OverlayKey} {selected.OverlayVersion}: saved detail is unavailable; owned files cannot be listed and this preview is partial. Reload before changing anything.");
        }
        else
        {
            var blocker = Blocker(selected, snapshot, hostKey, artifact);
            foreach (var owned in selectedDetail.Files.Where(f => f.IsEnabled))
            {
                var currentEntry = current.SingleOrDefault(f => string.Equals(f.File.RelativePath, owned.RelativePath, StringComparison.OrdinalIgnoreCase));
                var afterEntry = after.SingleOrDefault(f => string.Equals(f.File.RelativePath, owned.RelativePath, StringComparison.OrdinalIgnoreCase));
                var currentFile = currentEntry?.File; var afterFile = afterEntry?.File;
                if (blocker is not null)
                {
                    files.Add(new(owned.RelativePath, currentFile?.Winner, afterFile?.Winner, "Unchanged",
                        [$"{selected.OverlayKey} {selected.OverlayVersion}: {blocker} Removal changes nothing on this host and artifact."]));
                }
                else if (currentFile is null)
                {
                    files.Add(new(owned.RelativePath, null, afterFile?.Winner, "Unknown",
                        [$"{selected.OverlayKey} {selected.OverlayVersion}: no saved effective file at this path; coverage is unknown. Reload before changing anything."]));
                }
                else if (afterFile is null)
                {
                    files.Add(new(owned.RelativePath, currentFile.Winner, null, "NoEffectiveFile",
                        [$"{selected.OverlayKey} {selected.OverlayVersion}: no saved source remains for this path after removal."]));
                }
                else if (!SameSource(currentFile.Winner, afterFile.Winner))
                {
                    files.Add(new(owned.RelativePath, currentFile.Winner, afterFile.Winner, "FallsBack",
                        [$"{selected.OverlayKey} {selected.OverlayVersion}: {Describe(afterFile.Winner)} becomes the winner."]));
                }
                else if (!string.Equals(currentEntry!.ContentHash, afterEntry!.ContentHash, StringComparison.Ordinal))
                {
                    // Winner identity alone is not enough: a merge contribution
                    // below the winner changes the effective content even
                    // though the same source still wins.
                    files.Add(new(owned.RelativePath, currentFile.Winner, afterFile.Winner, "ChangesEffectiveContent",
                        [$"{selected.OverlayKey} {selected.OverlayVersion}: {Describe(afterFile.Winner)} still wins, but removing this document changes the merged effective content for this path."]));
                }
                else
                {
                    files.Add(new(owned.RelativePath, currentFile.Winner, afterFile.Winner, "Unchanged",
                        [$"{selected.OverlayKey} {selected.OverlayVersion}: does not decide this path; {Describe(afterFile.Winner)} still wins."]));
                }
            }
            if (files.Count == 0)
                diagnostics.Add($"{selected.OverlayKey} {selected.OverlayVersion}: no enabled owned files apply to this host and artifact.");
        }
        var fingerprint = ComputePreviewFingerprint(snapshot, selected, host, artifact, collected.ArtifactFiles, collected.OverlayFiles);
        return new(documentId, selected.OverlayKey, selected.OverlayVersion, selected.HostScope, selected.HostKey,
            selected.IsEnabled, host.HostKey, artifact.ArtifactId, selected.OverlaySha256, selected.UpdatedUtc,
            selectedDetail is null, files, diagnostics, fingerprint);
    }

    // Fingerprint over every saved row that can affect the previewed host and
    // artifact: all document rows (identity, selectors, hash, update time),
    // the host and artifact rows, the artifact files, and the files of every
    // enabled overlay that matches this host/artifact. File content enters
    // only as its SHA-256. Shared by the preview, the in-memory store and the
    // transactional store so all three derive the identical value.
    public static string ComputePreviewFingerprint(OverlayAdminSnapshot snapshot, ConfigOverlayDocumentRow selected,
        HostRow host, ArtifactRow artifact, IReadOnlyList<OverlaySourceRow> artifactFiles,
        IReadOnlyList<OverlaySourceRow> overlayFiles)
    {
        var builder = new StringBuilder();
        void Field(object? value) => builder.Append(value switch
        {
            null => "<null>",
            bool b => b ? "1" : "0",
            DateTime time => time.Ticks.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        }).Append('\u001f');
        void Document(ConfigOverlayDocumentRow row)
        {
            Field(row.ConfigOverlayDocumentId); Field(row.OverlayKey); Field(row.OverlayVersion); Field(row.HostKey);
            Field(row.HostScope); Field(row.ModuleKey); Field(row.ModuleDefinitionVersion); Field(row.AppKey);
            Field(row.PackageType); Field(row.TargetName); Field(row.ArtifactVersion); Field(row.OverlaySha256);
            Field(row.SourceName); Field(row.ConfigurationFileCount); Field(row.IsEnabled); Field(row.UpdatedUtc);
            builder.Append('\n');
        }
        void FileRow(OverlaySourceRow row)
        {
            Field(row.RowId); Field(row.DocumentId); Field(row.RelativePath); Field(row.MergeMode); Field(row.IsEnabled);
            Field(row.UpdatedUtc); Field(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.Content))));
            builder.Append('\n');
        }
        Field("v1"); Field(selected.ConfigOverlayDocumentId); Field(host.HostKey); Field(artifact.ArtifactId);
        Field(snapshot.SchemaReady); Field(snapshot.HasHostScope); builder.Append('\n');
        Field(host.HostKey); Field(host.IsEnabled); builder.Append('\n');
        Field(artifact.ArtifactId); Field(artifact.ModuleKey); Field(artifact.AppKey); Field(artifact.Version);
        Field(artifact.PackageType); Field(artifact.TargetName); Field(artifact.IsEnabled); builder.Append('\n');
        Document(selected);
        foreach (var row in snapshot.Overlays.OrderBy(o => o.ConfigOverlayDocumentId)) Document(row);
        foreach (var row in artifactFiles.OrderBy(f => f.RowId)) FileRow(row);
        foreach (var row in overlayFiles.OrderBy(f => f.DocumentId).ThenBy(f => f.RowId)) FileRow(row);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string? Blocker(ConfigOverlayDocumentRow selected, OverlayAdminSnapshot snapshot,
        string hostKey, ArtifactRow artifact)
    {
        if (!selected.IsEnabled) return "document is already disabled.";
        var allHosts = selected.HostScope == "AllHosts";
        if (!(allHosts && snapshot.HasHostScope) && !Equal(selected.HostKey, hostKey)) return "document targets another host.";
        if (!Matches(selected.ModuleKey, artifact.ModuleKey) || !Matches(selected.AppKey, artifact.AppKey)
            || !Matches(selected.PackageType, artifact.PackageType) || !Matches(selected.TargetName, artifact.TargetName))
            return "document selectors target another application.";
        if (selected.ArtifactVersion is not null && ArtifactVersionComparer.Compare(artifact.Version, selected.ArtifactVersion) < 0)
            return $"document is excluded by artifact minimum {selected.ArtifactVersion}.";
        return null;
    }

    private static bool SameSource(EffectiveConfigurationSourceDto a, EffectiveConfigurationSourceDto b) =>
        a.SourceKind == b.SourceKind && string.Equals(a.OverlayKey, b.OverlayKey, StringComparison.Ordinal)
        && string.Equals(a.OverlayVersion, b.OverlayVersion, StringComparison.Ordinal)
        && string.Equals(a.HostScope, b.HostScope, StringComparison.Ordinal)
        && string.Equals(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);

    private static string Describe(EffectiveConfigurationSourceDto winner) =>
        winner.SourceKind == "Artifact"
            ? $"artifact-owned configuration ({winner.RelativePath})"
            : $"{winner.SourceKind} {winner.OverlayKey} {winner.OverlayVersion} ({winner.HostScope})";

    private sealed record Collected(List<Candidate> Candidates, List<string> Diagnostics, HostRow Host,
        ArtifactRow Artifact, IReadOnlyList<OverlaySourceRow> ArtifactFiles, IReadOnlyList<OverlaySourceRow> OverlayFiles);

    // Relevance for one host/artifact, shared by candidate collection and by
    // the fingerprint a preview-bound mutation re-derives.
    internal static bool IsRelevantTo(ConfigOverlayDocumentRow overlay, OverlayAdminSnapshot snapshot,
        string hostKey, ArtifactRow artifact)
    {
        var allHosts = overlay.HostScope == "AllHosts";
        if (!(allHosts && snapshot.HasHostScope) && !Equal(overlay.HostKey, hostKey)) return false;
        if (!Matches(overlay.ModuleKey, artifact.ModuleKey) || !Matches(overlay.AppKey, artifact.AppKey)
            || !Matches(overlay.PackageType, artifact.PackageType) || !Matches(overlay.TargetName, artifact.TargetName)) return false;
        return true;
    }

    private async Task<Collected> CollectCandidatesAsync(OverlayAdminSnapshot snapshot, string hostKey, int artifactId, CancellationToken ct)
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
        var artifactFiles = await store.SourcesAsync(artifactId, ct);
        foreach (var file in artifactFiles)
        {
            if (file.IsEnabled) candidates.Add(new(file, null));
            else diagnostics.Add($"Artifact file {file.RelativePath}: disabled.");
        }
        var overlayFiles = new List<OverlaySourceRow>();
        foreach (var overlay in snapshot.Overlays)
        {
            if (!IsRelevantTo(overlay, snapshot, hostKey, artifact)) continue;
            if (!overlay.IsEnabled) { diagnostics.Add($"{overlay.OverlayKey} {overlay.OverlayVersion}: disabled."); continue; }
            if (overlay.ArtifactVersion is not null && ArtifactVersionComparer.Compare(artifact.Version, overlay.ArtifactVersion) < 0)
            {
                diagnostics.Add($"{overlay.OverlayKey} {overlay.OverlayVersion}: excluded by artifact minimum {overlay.ArtifactVersion}.");
                continue;
            }
            var detail = await store.DetailAsync(overlay.ConfigOverlayDocumentId, ct);
            if (detail is null) { diagnostics.Add($"{overlay.OverlayKey}: missing document; reload."); continue; }
            overlayFiles.AddRange(detail.Files);
            foreach (var file in detail.Files)
            {
                if (file.IsEnabled) candidates.Add(new(file, overlay));
                else diagnostics.Add($"{overlay.OverlayKey} / {file.RelativePath}: disabled file.");
            }
            if (detail.Files.Count == 0) diagnostics.Add($"{overlay.OverlayKey}: no configuration files.");
        }
        return new(candidates, diagnostics, host, artifact, artifactFiles, overlayFiles);
    }

    public sealed record Candidate(OverlaySourceRow File, ConfigOverlayDocumentRow? Overlay)
    {
        public int Priority => Overlay is null ? 0 : Overlay.HostScope == "AllHosts" ? 1 : 2;
    }

    internal sealed record ResolvedFile(EffectiveConfigurationFileDto File, string ContentHash);

    public static OverlayEffectiveReadback Resolve(IEnumerable<Candidate> candidates, List<string>? diagnostics = null)
    {
        diagnostics ??= [];
        var resolved = ResolveWithContent(candidates, diagnostics);
        if (resolved.Count == 0 && !diagnostics.Contains("No effective configuration files found."))
            diagnostics.Add("No effective configuration files found.");
        return new(resolved.Select(r => r.File).OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), diagnostics);
    }

    // Same resolution as Resolve, plus a hash of the actual merged effective
    // content per path. The hash stays inside the service; DTOs never carry it.
    internal static List<ResolvedFile> ResolveWithContent(IEnumerable<Candidate> candidates, List<string>? diagnostics = null)
    {
        diagnostics ??= [];
        var results = new List<ResolvedFile>();
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
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)));
            results.Add(new(new(group.Key, Dto(winner), group.OrderBy(c => c.Priority).Select(Dto).ToArray()), hash));
        }
        return results;
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
