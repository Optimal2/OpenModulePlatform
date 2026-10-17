using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

[RequestSizeLimit(2 * 1024 * 1024)]
public sealed class ConfigOverlaysModel(IOptions<WebAppOptions> options, RbacService rbac,
    IConfigOverlayAdminStore store, ConfigOverlayAdministration administration) : OmpPortalPageModel(options, rbac)
{
    public OverlayAdminSnapshot Snapshot { get; private set; } = new(false, [], [], []);
    public OverlayAdminDetail? Detail { get; private set; }
    public OverlayEffectiveReadback? Effective { get; private set; }
    [BindProperty] public DraftInput Draft { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? HostKey { get; set; }
    [BindProperty(SupportsGet = true)] public int? ArtifactId { get; set; }
    [TempData] public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int? id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null) return guard;
        await LoadAsync(ct);
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
            var hosts = Snapshot.Hosts.Where(h => string.Equals(h.HostKey, HostKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (hosts.Length == 1) Draft.HostKey = hosts[0].HostKey;
        }
        if (ArtifactId.HasValue && !string.IsNullOrWhiteSpace(HostKey))
        {
            try { Effective = await administration.ReadEffectiveAsync(Snapshot, HostKey, ArtifactId.Value, ct); }
            catch (InvalidOperationException) { ModelState.AddModelError(string.Empty, T("Select a registered host and artifact in this installation.")); }
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
            var id = await store.SaveAsync(document, Draft.Id, Draft.Token, ct);
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
    public Task<IActionResult> OnPostDisableAsync(int id, string token, CancellationToken ct) => ChangeAsync(id, token, false, ct);
    public Task<IActionResult> OnPostDeleteAsync(int id, string token, CancellationToken ct) => ChangeAsync(id, token, null, ct);

    private async Task<IActionResult> ChangeAsync(int id, string token, bool? enabled, CancellationToken ct)
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
        try
        {
            await store.ChangeStateAsync(id, token, enabled, ct);
            StatusMessage = T("Overlay updated. Live files have not been changed.");
            return RedirectToPage();
        }
        catch (Exception ex) when (ex is InvalidOperationException or SqlException)
        {
            ModelState.AddModelError(string.Empty, T("Overlay was not changed. Reload and retry; another administrator may have changed it."));
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        SetTitles("Configuration overlays");
        Snapshot = await store.ReadAsync(ct);
    }

    public static string Token(Models.ConfigOverlayDocumentRow row) => OmpAdminRepository.OverlayEditToken(row.OverlaySha256, row.UpdatedUtc);

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
