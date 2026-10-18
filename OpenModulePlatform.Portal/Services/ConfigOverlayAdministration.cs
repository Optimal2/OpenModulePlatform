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
    IReadOnlyList<ArtifactRow> Artifacts, IReadOnlyList<ConfigOverlayDocumentRow> Overlays, bool HasHostScope = true,
    IReadOnlyList<AppInstanceRow>? AppInstances = null);
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
// Installation-wide read-only impact for one saved overlay across the
// configured app contexts of this installation. Metadata only; file content
// is never included. Saved rows only: never live files, disk contents or
// final deployment configuration. Read-only: no tokens, no mutations.
public sealed record OverlayInstallationImpact(
    int DocumentId,
    string OverlayKey,
    string OverlayVersion,
    string HostScope,
    string HostKey,
    bool DocumentWasEnabled,
    bool DetailUnavailable,
    IReadOnlyList<OverlayInstallationContextDto> Contexts,
    IReadOnlyList<OverlayPossibleArtifactDto> PossibleArtifacts,
    int TotalConfiguredContexts,
    int OmittedContexts,
    int OmittedArtifacts,
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
                files.Add(ClassifyRemovalFile(selected, owned.RelativePath, currentEntry, afterEntry, blocker,
                    "on this host and artifact"));
            }
            if (files.Count == 0)
                diagnostics.Add($"{selected.OverlayKey} {selected.OverlayVersion}: no enabled owned files apply to this host and artifact.");
        }
        var fingerprint = ComputePreviewFingerprint(snapshot, selected, host, artifact, collected.ArtifactFiles, collected.OverlayFiles);
        return new(documentId, selected.OverlayKey, selected.OverlayVersion, selected.HostScope, selected.HostKey,
            selected.IsEnabled, host.HostKey, artifact.ArtifactId, selected.OverlaySha256, selected.UpdatedUtc,
            selectedDetail is null, files, diagnostics, fingerprint);
    }

    // Shared per-file removal classification for one owned path. Compares
    // both the winner and the merged effective content before and after
    // excluding the selected document. `location` names where the outcome
    // applies ("on this host and artifact" for the single preview, one
    // configured context for the installation impact).
    private static OverlayRemovalFilePreview ClassifyRemovalFile(ConfigOverlayDocumentRow selected,
        string relativePath, ResolvedFile? currentEntry, ResolvedFile? afterEntry, string? blocker, string location)
    {
        var currentFile = currentEntry?.File; var afterFile = afterEntry?.File;
        if (blocker is not null)
        {
            return new(relativePath, currentFile?.Winner, afterFile?.Winner, "Unchanged",
                [$"{selected.OverlayKey} {selected.OverlayVersion}: {blocker} Removal changes nothing {location}."]);
        }
        if (currentFile is null)
        {
            return new(relativePath, null, afterFile?.Winner, "Unknown",
                [$"{selected.OverlayKey} {selected.OverlayVersion}: no saved effective file at this path; coverage is unknown. Reload before changing anything."]);
        }
        if (afterFile is null)
        {
            return new(relativePath, currentFile.Winner, null, "NoEffectiveFile",
                [$"{selected.OverlayKey} {selected.OverlayVersion}: no saved source remains for this path after removal."]);
        }
        if (!SameSource(currentFile.Winner, afterFile.Winner))
        {
            return new(relativePath, currentFile.Winner, afterFile.Winner, "FallsBack",
                [$"{selected.OverlayKey} {selected.OverlayVersion}: {Describe(afterFile.Winner)} becomes the winner."]);
        }
        if (!string.Equals(currentEntry!.ContentHash, afterEntry!.ContentHash, StringComparison.Ordinal))
        {
            // Winner identity alone is not enough: a merge contribution
            // below the winner changes the effective content even
            // though the same source still wins.
            return new(relativePath, currentFile.Winner, afterFile.Winner, "ChangesEffectiveContent",
                [$"{selected.OverlayKey} {selected.OverlayVersion}: {Describe(afterFile.Winner)} still wins, but removing this document changes the merged effective content for this path."]);
        }
        return new(relativePath, currentFile.Winner, afterFile.Winner, "Unchanged",
            [$"{selected.OverlayKey} {selected.OverlayVersion}: does not decide this path; {Describe(afterFile.Winner)} still wins."]);
    }

    // Output bounds for the installation-wide impact view. Truncation is
    // never silent: omitted counts are reported in Diagnostics.
    public const int MaxInstallationContexts = 25;
    public const int MaxPossibleArtifacts = 25;

    // Installation-wide read-only impact for one saved overlay. Enumerates
    // the configured app contexts (one AppInstanceRow each: a concrete host
    // plus the exact artifact version it runs) and classifies every enabled
    // owned file with the same winner-plus-merged-content comparison as the
    // single host/artifact removal preview. Artifacts that match the overlay
    // selectors but are referenced by no configured app are listed separately
    // as possible matches, never as deployments. Metadata only; never
    // content. Read-only: no tokens, no mutations, no schema or store writes.
    public async Task<OverlayInstallationImpact> PreviewInstallationImpactAsync(
        OverlayAdminSnapshot snapshot, int documentId, CancellationToken ct)
    {
        var selected = snapshot.Overlays.SingleOrDefault(o => o.ConfigOverlayDocumentId == documentId)
            ?? throw new InvalidOperationException("Select a saved overlay in this installation.");
        var diagnostics = new List<string>
        {
            "Installation impact is read-only and covers saved rows only: never live files, disk contents or final deployment configuration.",
            "Only configured app contexts in this installation count as deployments. Matching artifacts with no configured app are listed separately as possible matches, never as deployments.",
        };
        if (!snapshot.SchemaReady) diagnostics.Add("Schema not ready: writes are disabled; upgrade the platform schema.");
        if (!snapshot.HasHostScope) diagnostics.Add("HostScope is missing: exact-host fallback only.");
        var appInstances = snapshot.AppInstances ?? [];
        var artifactsById = snapshot.Artifacts.ToDictionary(a => a.ArtifactId);
        var ordered = appInstances
            .OrderBy(a => a.HostKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.AppInstanceKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.ArtifactId)
            .ToList();

        var selectedDetail = await store.DetailAsync(documentId, ct);
        if (selectedDetail is null)
            diagnostics.Add($"{selected.OverlayKey} {selected.OverlayVersion}: saved detail is unavailable; owned files cannot be listed and this impact view is partial. Reload before changing anything.");

        // One saved-context read per referenced artifact and per enabled
        // overlay that can apply to at least one resolvable context.
        var artifactFiles = new Dictionary<int, IReadOnlyList<OverlaySourceRow>>();
        foreach (var artifactId in ordered.Where(a => a.ArtifactId.HasValue).Select(a => a.ArtifactId!.Value).Distinct())
            artifactFiles[artifactId] = await store.SourcesAsync(artifactId, ct);
        var resolvable = ordered
            .Where(a => a.HostKey is not null && a.ArtifactId.HasValue && artifactsById.ContainsKey(a.ArtifactId.Value))
            .Select(a => (HostKey: a.HostKey!, Artifact: artifactsById[a.ArtifactId!.Value]))
            .ToList();
        var overlayDetails = new List<(ConfigOverlayDocumentRow Overlay, IReadOnlyList<OverlaySourceRow> Files)>();
        foreach (var overlay in snapshot.Overlays)
        {
            if (!overlay.IsEnabled) continue;
            if (!resolvable.Any(r => IsRelevantTo(overlay, snapshot, r.HostKey, r.Artifact))) continue;
            var detail = await store.DetailAsync(overlay.ConfigOverlayDocumentId, ct);
            if (detail is null) continue;
            overlayDetails.Add((overlay, detail.Files));
        }

        var contexts = new List<OverlayInstallationContextDto>();
        foreach (var app in ordered.Take(MaxInstallationContexts))
            contexts.Add(BuildImpactContext(snapshot, selected, selectedDetail?.Files, app, artifactsById, artifactFiles, overlayDetails));
        var omittedContexts = Math.Max(0, ordered.Count - contexts.Count);
        if (omittedContexts > 0)
            diagnostics.Add($"Showing {contexts.Count} of {ordered.Count} configured app contexts; {omittedContexts} further context(s) omitted by bound. Refine selectors before changing.");

        var hostsWithoutContext = snapshot.Hosts
            .Where(h => !ordered.Any(a => string.Equals(a.HostKey, h.HostKey, StringComparison.OrdinalIgnoreCase)))
            .Select(h => h.HostKey)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (hostsWithoutContext.Count > 0)
            diagnostics.Add(hostsWithoutContext.Count <= 10
                ? $"Registered host(s) without a configured app context: {string.Join(", ", hostsWithoutContext)}. They have no deployment for this overlay to affect."
                : $"{hostsWithoutContext.Count} registered hosts have no configured app context. They have no deployment for this overlay to affect.");

        var referencedIds = new HashSet<int>(ordered.Where(a => a.ArtifactId.HasValue).Select(a => a.ArtifactId!.Value));
        var possible = snapshot.Artifacts
            .Where(a => !referencedIds.Contains(a.ArtifactId) && MatchesArtifactSelectors(selected, a))
            .OrderBy(a => a.ModuleKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.AppKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.ArtifactId)
            .ToList();
        var shownPossible = possible.Take(MaxPossibleArtifacts).Select(a =>
            new OverlayPossibleArtifactDto(a.ArtifactId, a.ModuleKey, a.AppKey, a.Version, a.PackageType, a.TargetName)).ToList();
        var omittedArtifacts = Math.Max(0, possible.Count - shownPossible.Count);
        if (omittedArtifacts > 0)
            diagnostics.Add($"{omittedArtifacts} further matching artifact(s) omitted by bound.");
        if (possible.Count == 0 && selectedDetail is not null)
            diagnostics.Add(omittedContexts > 0
                ? $"No unreferenced artifact matches this overlay's selectors among the shown contexts; {omittedContexts} configured context(s) were omitted by bound, so a match on an omitted context is not ruled out."
                : "No unreferenced artifact matches this overlay's selectors; every matching artifact is either configured on an app context above or matches nothing.");

        return new(documentId, selected.OverlayKey, selected.OverlayVersion, selected.HostScope, selected.HostKey,
            selected.IsEnabled, selectedDetail is null, contexts, shownPossible,
            ordered.Count, omittedContexts, omittedArtifacts, diagnostics);
    }

    private static OverlayInstallationContextDto BuildImpactContext(OverlayAdminSnapshot snapshot,
        ConfigOverlayDocumentRow selected, IReadOnlyList<OverlaySourceRow>? ownedFiles, AppInstanceRow app,
        IReadOnlyDictionary<int, ArtifactRow> artifactsById,
        IReadOnlyDictionary<int, IReadOnlyList<OverlaySourceRow>> artifactFiles,
        IReadOnlyList<(ConfigOverlayDocumentRow Overlay, IReadOnlyList<OverlaySourceRow> Files)> overlayDetails)
    {
        var notes = new List<string>();
        if (app.HostKey is null)
            return new("(no host assigned)", app.AppKey, app.AppInstanceKey, app.ArtifactId, null,
                "Unavailable", [], [$"App context '{app.AppInstanceKey}' has no assigned host in this installation; nothing can be resolved for it."]);
        var hosts = snapshot.Hosts.Where(h => string.Equals(h.HostKey, app.HostKey, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hosts.Count != 1)
            return new(app.HostKey, app.AppKey, app.AppInstanceKey, app.ArtifactId, null,
                "Unavailable", [], [hosts.Count == 0
                    ? $"Host '{app.HostKey}' is not registered in this installation; nothing can be resolved for app context '{app.AppInstanceKey}'."
                    : $"Host '{app.HostKey}' has {hosts.Count} rows in this installation; overlay identity uses the host key alone. Resolve duplicate host keys before trusting this context."]);
        if (!app.ArtifactId.HasValue || !artifactsById.TryGetValue(app.ArtifactId.Value, out var artifact))
            return new(app.HostKey, app.AppKey, app.AppInstanceKey, app.ArtifactId, null,
                "Unavailable", [], [$"App context '{app.AppInstanceKey}' on host '{app.HostKey}' has no registered artifact in this installation; nothing can be resolved for it."]);
        var host = hosts[0];
        if (!host.IsEnabled) notes.Add($"Host '{host.HostKey}' is disabled; this describes saved configuration only.");
        if (!artifact.IsEnabled) notes.Add($"Artifact {artifact.ArtifactId} ({artifact.Version}) is disabled; this describes saved configuration only.");
        var candidates = BuildImpactCandidates(snapshot, host.HostKey, artifact,
            artifactFiles.GetValueOrDefault(artifact.ArtifactId, []), overlayDetails, notes, snapshot.Overlays);
        // Same Contains-based dedup as the single-host preview: a
        // merge-failure diagnostic occurring in both resolutions is emitted
        // once per context, never twice.
        var currentDiagnostics = new List<string>();
        var current = ResolveWithContent(candidates, currentDiagnostics);
        var afterDiagnostics = new List<string>();
        var after = ResolveWithContent(candidates.Where(c => c.Overlay?.ConfigOverlayDocumentId != selected.ConfigOverlayDocumentId), afterDiagnostics);
        foreach (var message in currentDiagnostics.Concat(afterDiagnostics))
            if (!notes.Contains(message)) notes.Add(message);
        var files = new List<OverlayInstallationFileDto>();
        var location = $"on host '{host.HostKey}', app '{app.AppInstanceKey}', artifact {artifact.ArtifactId} ({artifact.Version})";
        if (ownedFiles is null)
        {
            notes.Add($"{selected.OverlayKey} {selected.OverlayVersion}: saved detail is unavailable; owned files cannot be listed for this context.");
        }
        else
        {
            var blocker = Blocker(selected, snapshot, host.HostKey, artifact);
            foreach (var owned in ownedFiles.Where(f => f.IsEnabled))
            {
                var currentEntry = current.SingleOrDefault(f => string.Equals(f.File.RelativePath, owned.RelativePath, StringComparison.OrdinalIgnoreCase));
                var afterEntry = after.SingleOrDefault(f => string.Equals(f.File.RelativePath, owned.RelativePath, StringComparison.OrdinalIgnoreCase));
                var classified = ClassifyRemovalFile(selected, owned.RelativePath, currentEntry, afterEntry, blocker, location);
                files.Add(new(classified.RelativePath, classified.CurrentWinner, classified.WinnerAfterRemoval, classified.Outcome, classified.Notes));
            }
            if (files.Count == 0)
                notes.Add(blocker is not null
                    ? $"{selected.OverlayKey} {selected.OverlayVersion}: {blocker} It has no effect {location}."
                    : $"{selected.OverlayKey} {selected.OverlayVersion}: no enabled owned files apply {location}.");
        }
        // Unavailable contexts return above; here the host and artifact rows
        // exist, so the state follows the same selector/minimum/version gate
        // as the single host/artifact preview.
        var state = Blocker(selected, snapshot, host.HostKey, artifact) is not null ? "NotApplicable" : "Applicable";
        return new(host.HostKey, app.AppKey, app.AppInstanceKey, artifact.ArtifactId, artifact.Version, state, files, notes);
    }

    private static List<Candidate> BuildImpactCandidates(OverlayAdminSnapshot snapshot, string hostKey,
        ArtifactRow artifact, IReadOnlyList<OverlaySourceRow> artifactRows,
        IReadOnlyList<(ConfigOverlayDocumentRow Overlay, IReadOnlyList<OverlaySourceRow> Files)> overlayDetails,
        List<string> notes, IReadOnlyList<ConfigOverlayDocumentRow> overlays)
    {
        var candidates = new List<Candidate>();
        foreach (var file in artifactRows)
        {
            if (file.IsEnabled) candidates.Add(new(file, null));
            else notes.Add($"Artifact file {file.RelativePath}: disabled.");
        }
        foreach (var overlay in overlays)
        {
            if (!IsRelevantTo(overlay, snapshot, hostKey, artifact)) continue;
            if (!overlay.IsEnabled) { notes.Add($"{overlay.OverlayKey} {overlay.OverlayVersion}: disabled."); continue; }
            if (overlay.ArtifactVersion is not null && ArtifactVersionComparer.Compare(artifact.Version, overlay.ArtifactVersion) < 0)
            {
                notes.Add($"{overlay.OverlayKey} {overlay.OverlayVersion}: excluded by artifact minimum {overlay.ArtifactVersion}.");
                continue;
            }
            var files = overlayDetails.FirstOrDefault(d => d.Overlay.ConfigOverlayDocumentId == overlay.ConfigOverlayDocumentId).Files;
            if (files is null) { notes.Add($"{overlay.OverlayKey}: missing document; reload."); continue; }
            foreach (var file in files)
            {
                if (file.IsEnabled) candidates.Add(new(file, overlay));
                else notes.Add($"{overlay.OverlayKey} / {file.RelativePath}: disabled file.");
            }
            if (files.Count == 0) notes.Add($"{overlay.OverlayKey}: no configuration files.");
        }
        return candidates;
    }

    private static bool MatchesArtifactSelectors(ConfigOverlayDocumentRow overlay, ArtifactRow artifact) =>
        Matches(overlay.ModuleKey, artifact.ModuleKey) && Matches(overlay.AppKey, artifact.AppKey)
        && Matches(overlay.PackageType, artifact.PackageType) && Matches(overlay.TargetName, artifact.TargetName)
        && (overlay.ArtifactVersion is null || ArtifactVersionComparer.Compare(artifact.Version, overlay.ArtifactVersion) >= 0);

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
