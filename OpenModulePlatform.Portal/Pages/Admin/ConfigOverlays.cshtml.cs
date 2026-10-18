using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

[RequestSizeLimit(2 * 1024 * 1024)]
public sealed class ConfigOverlaysModel(IOptions<WebAppOptions> options, RbacService rbac,
    IConfigOverlayAdminStore store, ConfigOverlayAdministration administration) : OmpPortalPageModel(options, rbac)
{
    private const long MaxUploadedFileBytes = 1024 * 1024;

    public OverlayAdminSnapshot Snapshot { get; private set; } = new(false, [], [], []);
    public OverlayAdminDetail? Detail { get; private set; }
    public OverlayEffectiveReadback? Effective { get; private set; }
    public OverlayRemovalPreview? RemovalPreview { get; private set; }
    public OverlayInstallationImpact? InstallationImpact { get; private set; }
    public OverlayContentComparison? ContentComparison { get; private set; }
    public IReadOnlyList<OverlayDeletedHistoryRow> DeletedHistory { get; private set; } = [];
    public IReadOnlyList<ConfigOverlayDocumentRow> FilteredOverlays { get; private set; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<OverlayFileSummaryRow>> FileSummariesByDocument { get; private set; }
        = new Dictionary<int, IReadOnlyList<OverlayFileSummaryRow>>();
    [BindProperty] public DraftInput Draft { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? HostKey { get; set; }
    [BindProperty(SupportsGet = true)] public int? ArtifactId { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterHost { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterModule { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterApp { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterPackage { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterTarget { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterEnabled { get; set; }
    [BindProperty(SupportsGet = true)] public string? FilterScope { get; set; }
    [BindProperty] public IFormFile? UploadedFile { get; set; }
    [BindProperty] public string? DraftFileRelativePath { get; set; }
    [TempData] public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int? id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null) return guard;
        await LoadAsync(ct);
        var requestedHostKey = HostKey;
        var hosts = Snapshot.Hosts.Where(h => string.Equals(h.HostKey, requestedHostKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        HostKey = hosts.Length == 1 ? hosts[0].HostKey : null;
        // Render only a unique installation-local identity, rather than the raw query value.
        ModelState.Remove(nameof(HostKey));
        FileSummariesByDocument = (Snapshot.FileSummaries ?? [])
            .GroupBy(f => f.ConfigOverlayDocumentId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<OverlayFileSummaryRow>)g.ToList());
        FilteredOverlays = FilterOverlays(Snapshot.Overlays, FilterHost, FilterModule, FilterApp, FilterPackage,
            FilterTarget, FilterEnabled, FilterScope);
        DeletedHistory = await store.DeletedHistoryAsync(ct);
        if (id.HasValue)
        {
            if (!Snapshot.Overlays.Any(o => o.ConfigOverlayDocumentId == id.Value)) return NotFound();
            Detail = await store.DetailAsync(id.Value, ct);
            if (Detail is null) return NotFound();
            Draft = new() { Id = id, Scope = Detail.Summary.HostScope, HostKey = Detail.Summary.HostKey,
                Json = Detail.Json, Token = Token(Detail.Summary) };
        }
        else
        {
            Draft.HostKey = HostKey;
        }
        if (ArtifactId.HasValue && !string.IsNullOrWhiteSpace(requestedHostKey))
        {
            try { Effective = await administration.ReadEffectiveAsync(Snapshot, requestedHostKey, ArtifactId.Value, ct); }
            catch (InvalidOperationException) { ModelState.AddModelError(string.Empty, T("Select a registered host and artifact in this installation.")); }
            // Read-only base/effective content comparison for the same
            // selection. Content is bounded and rendered inert by the view.
            try { ContentComparison = await administration.CompareContentAsync(Snapshot, requestedHostKey, ArtifactId.Value, ct); }
            catch (InvalidOperationException) { ModelState.AddModelError(string.Empty, T("Select a registered host and artifact in this installation.")); }
        }
        if (id.HasValue && ArtifactId.HasValue && !string.IsNullOrWhiteSpace(requestedHostKey)
            && Snapshot.Overlays.Any(o => o.ConfigOverlayDocumentId == id.Value))
        {
            // Read-only removal preview for the opened overlay on the selected
            // host and artifact. Disable/Delete still re-check the document
            // token, so a stale preview cannot silently mutate.
            try { RemovalPreview = await administration.PreviewRemovalAsync(Snapshot, id.Value, requestedHostKey, ArtifactId.Value, ct); }
            catch (InvalidOperationException) { ModelState.AddModelError(string.Empty, T("Select a registered host and artifact in this installation.")); }
        }
        if (id.HasValue && Snapshot.Overlays.Any(o => o.ConfigOverlayDocumentId == id.Value))
        {
            // Installation-wide read-only impact for the opened overlay. No
            // selection needed and no mutation is offered from this section.
            try { InstallationImpact = await administration.PreviewInstallationImpactAsync(Snapshot, id.Value, ct); }
            catch (InvalidOperationException) { ModelState.AddModelError(string.Empty, T("Select a saved overlay in this installation.")); }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null) return guard;
        await LoadAsync(ct);
        if (Draft.Id.HasValue)
        {
            if (!Snapshot.Overlays.Any(o => o.ConfigOverlayDocumentId == Draft.Id.Value)) return NotFound();
            Detail = await store.DetailAsync(Draft.Id.Value, ct);
            if (Detail is null) return NotFound();
        }
        if (!ModelState.IsValid) return Page();
        try
        {
            var document = ConfigOverlayAdministration.ValidateDraft(Draft.Json, Draft.Scope, Draft.HostKey, Snapshot, Detail?.Summary);
            var id = await store.SaveAsync(document, Draft.Id, Draft.Token, Actor(), ct);
            StatusMessage = T("Overlay saved. Reload saved sources to inspect precedence. Live files have not been changed.");
            if (document.SqlScriptCount > 0)
                StatusMessage += " " + T(Artifacts.ConfigOverlayPackageReader.ConfigOverlaySqlScriptsWarning);
            return RedirectToPage(new { id });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or SqlException)
        {
            // Do not echo parser, SQL, content or connection details to status/logs.
            ModelState.AddModelError(string.Empty, T("Overlay was not saved. Check JSON, inline files, scope and unchanged identity. If it changed elsewhere, reload before retrying. The platform schema must be ready."));
            return Page();
        }
    }

    public Task<IActionResult> OnPostEnableAsync(int id, string token, CancellationToken ct) => ChangeAsync(id, token, true, ct);
    public Task<IActionResult> OnPostDisableAsync(int id, string token, string? hostKey = null, int? artifactId = null,
        string? previewToken = null, CancellationToken ct = default) => ChangeAsync(id, token, false, ct, hostKey, artifactId, previewToken);
    public Task<IActionResult> OnPostDeleteAsync(int id, string token, string? hostKey = null, int? artifactId = null,
        string? previewToken = null, CancellationToken ct = default) => ChangeAsync(id, token, null, ct, hostKey, artifactId, previewToken);

    private async Task<IActionResult> ChangeAsync(int id, string token, bool? enabled, CancellationToken ct,
        string? hostKey = null, int? artifactId = null, string? previewToken = null)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null) return guard;
        await LoadAsync(ct);
        if (!Snapshot.Overlays.Any(o => o.ConfigOverlayDocumentId == id)) return NotFound();
        if (!Snapshot.SchemaReady)
        {
            ModelState.AddModelError(string.Empty, T("Upgrade the platform schema before editing overlays."));
            return Page();
        }
        // Disable and delete always go through the removal preview so the
        // installation impact is visible first; enable remains a direct
        // operation bound to the selected-row token.
        if (enabled is not true && string.IsNullOrWhiteSpace(previewToken))
        {
            ModelState.AddModelError(string.Empty, T("Preview the removal impact before disabling or deleting this overlay."));
            return Page();
        }
        try
        {
            if (enabled is true)
                await store.ChangeStateAsync(id, token, enabled, Actor(), ct);
            else
                await administration.ChangeStateFromPreviewAsync(Snapshot, id, hostKey ?? string.Empty,
                    artifactId ?? 0, token, enabled, previewToken!, Actor(), ct);
            StatusMessage = T("Overlay updated. Live files have not been changed.");
            return RedirectToPage();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SqlException)
        {
            ModelState.AddModelError(string.Empty, T("Overlay was not changed. Reload and retry; another administrator may have changed it."));
            return Page();
        }
    }

    public async Task<IActionResult> OnPostUploadFileAsync(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null) return guard;
        await LoadAsync(ct);
        if (Draft.Id.HasValue)
        {
            if (!Snapshot.Overlays.Any(o => o.ConfigOverlayDocumentId == Draft.Id.Value)) return NotFound();
            Detail = await store.DetailAsync(Draft.Id.Value, ct);
            if (Detail is null) return NotFound();
        }
        var relativePath = NormalizeDraftRelativePath(DraftFileRelativePath);
        if (relativePath.Length == 0)
        {
            ModelState.AddModelError(nameof(DraftFileRelativePath), T("Enter a safe relative path for the uploaded file."));
            return Page();
        }
        if (UploadedFile is null || UploadedFile.Length == 0)
        {
            ModelState.AddModelError(nameof(UploadedFile), T("Select a file to upload."));
            return Page();
        }
        try
        {
            var content = await ReadUploadedTextFileAsync(UploadedFile, ct);
            Draft.Json = ConfigOverlayAdministration.SetDraftFileContent(Draft.Json, relativePath, content);
            StatusMessage = T("Uploaded file content added to the draft at the chosen relative path. Save to persist.");
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException)
        {
            ModelState.AddModelError(nameof(UploadedFile), T("The uploaded file was not read. It must be a single UTF-8 text file of at most 1 MB, or a .zip containing one such file."));
        }
        return Page();
    }

    private async Task<string> ReadUploadedTextFileAsync(IFormFile file, CancellationToken ct)
        => IsZipUpload(file)
            ? await ReadUploadedZipTextFileAsync(file, ct)
            : await ReadTextStreamAsync(file.OpenReadStream(), ct);

    private static bool IsZipUpload(IFormFile file)
    {
        var fileName = Path.GetFileName(file.FileName);
        return fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.ContentType, "application/zip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.ContentType, "application/x-zip-compressed", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ReadUploadedZipTextFileAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (entries.Length != 1 || entries[0].Length > MaxUploadedFileBytes)
            throw new InvalidDataException();
        await using var entryStream = entries[0].Open();
        return await ReadTextStreamAsync(entryStream, ct);
    }

    private static async Task<string> ReadTextStreamAsync(Stream stream, CancellationToken ct)
    {
        await using (stream)
        {
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(ct);
        }
    }

    private static string NormalizeDraftRelativePath(string? relativePath)
        => (relativePath ?? string.Empty).Trim().Replace('\\', '/').Trim('/');

    private static IReadOnlyList<ConfigOverlayDocumentRow> FilterOverlays(IReadOnlyList<ConfigOverlayDocumentRow> overlays,
        string? host, string? module, string? app, string? package, string? target, string? enabled, string? scope)
    {
        IEnumerable<ConfigOverlayDocumentRow> rows = overlays;
        // A host filter also lists AllHosts rows: they are the defaults that
        // apply to that host.
        if (!string.IsNullOrWhiteSpace(host))
            rows = rows.Where(o => string.Equals(o.HostKey, host, StringComparison.OrdinalIgnoreCase)
                || string.Equals(o.HostScope, "AllHosts", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(module))
            rows = rows.Where(o => string.Equals(o.ModuleKey, module, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(app))
            rows = rows.Where(o => string.Equals(o.AppKey, app, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(package))
            rows = rows.Where(o => string.Equals(o.PackageType, package, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(target))
            rows = rows.Where(o => string.Equals(o.TargetName, target, StringComparison.OrdinalIgnoreCase));
        if (enabled == "enabled") rows = rows.Where(o => o.IsEnabled);
        else if (enabled == "disabled") rows = rows.Where(o => !o.IsEnabled);
        if (scope == "AllHosts") rows = rows.Where(o => string.Equals(o.HostScope, "AllHosts", StringComparison.OrdinalIgnoreCase));
        else if (scope == "Host") rows = rows.Where(o => !string.Equals(o.HostScope, "AllHosts", StringComparison.OrdinalIgnoreCase));
        return rows.ToList();
    }

    private string Actor() => User.Identity?.Name ?? "PortalAdmin";

    private async Task LoadAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        SetTitles("Configuration overlays");
        Snapshot = await store.ReadAsync(ct);
    }

    public static string Token(Models.ConfigOverlayDocumentRow row) => OmpAdminRepository.OverlayEditToken(row.OverlaySha256, row.UpdatedUtc);

    // Token the preview was computed against; identical to the token the
    // Disable/Delete forms submit, so operators can see when a preview is stale.
    public string? PreviewToken() => RemovalPreview is null
        ? null : OmpAdminRepository.OverlayEditToken(RemovalPreview.OverlaySha256, RemovalPreview.UpdatedUtc);

    // Per-action token over the preview fingerprint, rendered into the
    // preview section's own Disable/Delete forms.
    public string? PreviewActionToken(bool? enabled) => RemovalPreview is null
        ? null : Services.ConfigOverlayAdministration.PreviewActionToken(RemovalPreview.PreviewFingerprint, enabled);

    public sealed class DraftInput
    {
        public int? Id { get; set; }
        public string? Token { get; set; }
        [Required] public string Scope { get; set; } = "Host";
        public string? HostKey { get; set; }
        [Required, StringLength(1024 * 1024)] public string Json { get; set; } = """
            {
              "formatVersion": 1,
              "objectType": "config-overlay",
              "overlayKey": "example-settings",
              "overlayVersion": "1.0.0",
              "configurationFiles": [
                { "relativePath": "appsettings.json", "fileContent": "{}", "mergeMode": "merge" }
              ]
            }
            """;
    }
}
