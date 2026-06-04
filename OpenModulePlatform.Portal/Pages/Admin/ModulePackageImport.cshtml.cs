// File: OpenModulePlatform.Portal/Pages/Admin/ModulePackageImport.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Text.Json;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Imports and exports OMP portable objects through universal module packages.
/// </summary>
public sealed class ModulePackageImportModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;
    private readonly PortableModulePackageService _packages;
    private readonly PortalDashboardWidgetPackageService _widgets;
    private readonly ConfigOverlayObjectService _configObjects;

    public ModulePackageImportModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo,
        PortableModulePackageService packages,
        PortalDashboardWidgetPackageService widgets,
        ConfigOverlayObjectService configObjects)
        : base(options, rbac)
    {
        _repo = repo;
        _packages = packages;
        _widgets = widgets;
        _configObjects = configObjects;
    }

    [BindProperty]
    public UniversalUploadInputModel UniversalUploadInput { get; set; } = new();

    [BindProperty]
    public UniversalStagedImportInputModel UniversalStagedInput { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public UniversalExportInputModel UniversalExportInput { get; set; } = new();

    public IReadOnlyList<ModuleDefinitionDocumentRow> AppliedDefinitions { get; private set; } = [];

    public IReadOnlyList<ArtifactRow> ArtifactRows { get; private set; } = [];

    public IReadOnlyList<DashboardWidgetAdminRow> WidgetRows { get; private set; } = [];

    public IReadOnlyList<HostConfigurationDocumentRow> HostConfigurationRows { get; private set; } = [];

    public IReadOnlyList<ConfigOverlayDocumentRow> ConfigOverlayRows { get; private set; } = [];

    public UniversalPackagePreviewResult? UniversalPreview { get; private set; }

    public string ActivePanel { get; private set; } = string.Empty;

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Import/export");
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUpload(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnPostImportUniversal(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Import/export");
        try
        {
            var result = await _packages.ImportUniversalPackageUploadAsync(
                UniversalUploadInput.PackageFile,
                CreateOptions(UniversalUploadInput),
                UniversalUploadInput.ReplaceExistingConfigObjects,
                ct);

            StatusMessage = BuildUniversalImportStatus(result);
            return RedirectToPage("/Admin/ModulePackageImport");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or SqlException or UnauthorizedAccessException)
        {
            ActivePanel = "import-universal";
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostPreviewUniversal(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Import/export");
        try
        {
            UniversalPreview = await _packages.StageUniversalPackageUploadForPreviewAsync(
                UniversalUploadInput.PackageFile,
                ct);
            UniversalStagedInput = UniversalStagedImportInputModel.From(UniversalUploadInput, UniversalPreview.Token);
            ActivePanel = "import-universal";
            await LoadAsync(ct);
            return Page();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or SqlException or UnauthorizedAccessException)
        {
            ActivePanel = "import-universal";
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostImportStagedUniversal(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Import/export");
        try
        {
            var result = await _packages.ImportStagedUniversalPackageAsync(
                UniversalStagedInput.Token,
                UniversalStagedInput.SelectedItemPaths,
                CreateOptions(UniversalStagedInput),
                UniversalStagedInput.ReplaceExistingConfigObjects,
                ct);

            StatusMessage = BuildUniversalImportStatus(result);
            return RedirectToPage("/Admin/ModulePackageImport");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or SqlException or UnauthorizedAccessException)
        {
            ActivePanel = "import-universal";
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostImportArtifacts(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnPostImportWidgets(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnPostImportHostConfiguration(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnPostImportConfigOverlay(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnPostImportAvailable(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnPostImportAvailableHostConfiguration(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnPostImportAvailableConfigOverlay(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnGetExportArtifact(int artifactId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnGetExportWidgets(int? widgetId, string? moduleKey, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnGetExportUniversal(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var result = await _packages.ExportUniversalPackageAsync(
                UniversalExportInput.ToRequest(),
                ct);
            Response.OnCompleted(static state =>
            {
                TryDeleteTemporaryFile((string)state);
                return Task.CompletedTask;
            }, result.PackagePath);

            return PhysicalFile(result.PackagePath, "application/zip", result.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(T(ex.Message));
        }
    }

    public async Task<IActionResult> OnGetExportHostConfiguration(int documentId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnGetExportConfigOverlay(int documentId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnGetExport(
        string moduleKey,
        bool includeAllVersions,
        CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    public async Task<IActionResult> OnPostExportModuleToLibrary(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        return LegacyPortableFormatDisabled();
    }

    private IActionResult LegacyPortableFormatDisabled()
    {
        StatusMessage = T("Only universal module packages are supported for import and export. Use the Universal package workflow.");
        return RedirectToPage("/Admin/ModulePackageImport");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var definitions = await _repo.GetModuleDefinitionDocumentsAsync(ct);
        ArtifactRows = await _repo.GetArtifactsAsync(ct);
        HostConfigurationRows = await _repo.GetHostConfigurationDocumentsAsync(ct);
        ConfigOverlayRows = await _repo.GetConfigOverlayDocumentsAsync(ct);
        var widgets = await _widgets.GetWidgetsAsync(null, ct);
        WidgetRows = widgets
            .OrderBy(static row => row.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.WidgetKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        AppliedDefinitions = definitions
            .Where(static row => row.IsApplied)
            .GroupBy(static row => row.ModuleKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(row => row.AppliedUtc)
                .ThenByDescending(row => row.UpdatedUtc)
            .First())
            .OrderBy(static row => row.ModuleKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string BuildUniversalImportStatus(UniversalPackageImportResult result)
    {
        var packageName = string.IsNullOrWhiteSpace(result.PackageKey)
            ? result.SourceName
            : $"{result.PackageKey} {result.PackageVersion}".Trim();
        var message = string.Format(
            T("Imported universal module package {0}. Imported/updated: {1}. Skipped: {2}. Failed: {3}."),
            packageName,
            result.ImportedCount,
            result.SkippedCount,
            result.FailedCount);
        if (!string.IsNullOrWhiteSpace(result.TargetHostProfile))
        {
            message += " " + string.Format(
                T("Target host profile: {0}."),
                result.TargetHostProfile);
        }

        var detailRows = result.Items
            .Where(static item =>
                string.Equals(item.Status, "Failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Status, "Skipped", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(item.Message))
            .Select(static item => string.IsNullOrWhiteSpace(item.Message)
                ? $"{item.Kind} {item.Path}: {item.Status}"
                : $"{item.Kind} {item.Path}: {item.Status} - {item.Message}")
            .ToArray();
        if (detailRows.Length > 0)
        {
            message += " " + T("Import details:") + " " + string.Join(" | ", detailRows);
        }

        return message;
    }

    private static PortableModulePackageImportOptions CreateOptions(UniversalUploadInputModel input)
        => new(
            input.ApplyModuleDefinitions,
            input.ExecuteSqlRepairs,
            input.AllowTemporaryIncompatibleArtifacts,
            input.ReplaceExistingModuleDefinitions,
            input.ReplaceExistingArtifacts,
            input.CopyConfigurationFilesFromPreviousVersion,
            input.UseArtifactsImmediately);

    private static PortableModulePackageImportOptions CreateOptions(UniversalStagedImportInputModel input)
        => new(
            input.ApplyModuleDefinitions,
            input.ExecuteSqlRepairs,
            input.AllowTemporaryIncompatibleArtifacts,
            input.ReplaceExistingModuleDefinitions,
            input.ReplaceExistingArtifacts,
            input.CopyConfigurationFilesFromPreviousVersion,
            input.UseArtifactsImmediately);

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup for a temporary export file; a later temp cleanup can remove it if the file is still locked.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for a temporary export file; failing the completed response would not help the operator.
        }
    }

    public sealed class UniversalUploadInputModel
    {
        public IFormFile? PackageFile { get; set; }

        public bool ApplyModuleDefinitions { get; set; } = true;

        public bool ExecuteSqlRepairs { get; set; } = true;

        public bool AllowTemporaryIncompatibleArtifacts { get; set; } = true;

        public bool ReplaceExistingModuleDefinitions { get; set; }

        public bool ReplaceExistingArtifacts { get; set; }

        public bool ReplaceExistingConfigObjects { get; set; }

        public bool CopyConfigurationFilesFromPreviousVersion { get; set; } = true;

        public bool UseArtifactsImmediately { get; set; } = true;
    }

    public sealed class UniversalStagedImportInputModel
    {
        public string Token { get; set; } = string.Empty;

        public List<string> SelectedItemPaths { get; set; } = [];

        public bool ApplyModuleDefinitions { get; set; } = true;

        public bool ExecuteSqlRepairs { get; set; } = true;

        public bool AllowTemporaryIncompatibleArtifacts { get; set; } = true;

        public bool ReplaceExistingModuleDefinitions { get; set; }

        public bool ReplaceExistingArtifacts { get; set; }

        public bool ReplaceExistingConfigObjects { get; set; }

        public bool CopyConfigurationFilesFromPreviousVersion { get; set; } = true;

        public bool UseArtifactsImmediately { get; set; } = true;

        public static UniversalStagedImportInputModel From(
            UniversalUploadInputModel input,
            string token)
            => new()
            {
                Token = token,
                ApplyModuleDefinitions = input.ApplyModuleDefinitions,
                ExecuteSqlRepairs = input.ExecuteSqlRepairs,
                AllowTemporaryIncompatibleArtifacts = input.AllowTemporaryIncompatibleArtifacts,
                ReplaceExistingModuleDefinitions = input.ReplaceExistingModuleDefinitions,
                ReplaceExistingArtifacts = input.ReplaceExistingArtifacts,
                ReplaceExistingConfigObjects = input.ReplaceExistingConfigObjects,
                CopyConfigurationFilesFromPreviousVersion = input.CopyConfigurationFilesFromPreviousVersion,
                UseArtifactsImmediately = input.UseArtifactsImmediately
            };
    }

    public sealed class UniversalExportInputModel
    {
        public string PackageKey { get; set; } = "omp-universal";

        public string PackageVersion { get; set; } = DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);

        public string DisplayName { get; set; } = "OpenModulePlatform universal package";

        public string? Description { get; set; }

        public string? TargetHostProfile { get; set; }

        public List<string> ModuleKeys { get; set; } = [];

        public bool IncludeArtifactsForSelectedModules { get; set; } = true;

        public bool IncludeAllArtifactVersions { get; set; }

        public List<int> ArtifactIds { get; set; } = [];

        public List<int> HostConfigurationDocumentIds { get; set; } = [];

        public List<int> ConfigOverlayDocumentIds { get; set; } = [];

        public List<int> WidgetIds { get; set; } = [];

        public bool IncludeWidgetRuntimeData { get; set; }

        public UniversalPackageExportRequest ToRequest()
            => new(
                PackageKey,
                PackageVersion,
                DisplayName,
                Description,
                TargetHostProfile,
                ModuleKeys,
                IncludeArtifactsForSelectedModules,
                IncludeAllArtifactVersions,
                ArtifactIds,
                HostConfigurationDocumentIds,
                ConfigOverlayDocumentIds,
                WidgetIds,
                IncludeWidgetRuntimeData);
    }

}
