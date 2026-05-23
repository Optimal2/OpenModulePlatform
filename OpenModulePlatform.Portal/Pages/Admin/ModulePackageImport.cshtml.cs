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
/// Imports and exports the OMP-wide portable objects that can be moved between
/// installations: module packages, artifact packages, and dashboard widgets.
/// </summary>
public sealed class ModulePackageImportModel : OmpPortalPageModel
{
    private const int MaxWidgetJsonBytes = 1024 * 1024;

    private readonly OmpAdminRepository _repo;
    private readonly PortableModulePackageService _packages;
    private readonly PortalDashboardWidgetPackageService _widgets;

    public ModulePackageImportModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo,
        PortableModulePackageService packages,
        PortalDashboardWidgetPackageService widgets)
        : base(options, rbac)
    {
        _repo = repo;
        _packages = packages;
        _widgets = widgets;
    }

    [BindProperty]
    public UploadInputModel UploadInput { get; set; } = new();

    [BindProperty]
    public ArtifactUploadInputModel ArtifactUploadInput { get; set; } = new();

    [BindProperty]
    public WidgetUploadInputModel WidgetUploadInput { get; set; } = new();

    [BindProperty]
    public ImportInputModel ImportInput { get; set; } = new();

    public IReadOnlyList<AvailablePackageRowModel> AvailablePackages { get; private set; } = [];

    public IReadOnlyList<ModuleDefinitionDocumentRow> AppliedDefinitions { get; private set; } = [];

    public IReadOnlyList<ArtifactRow> ArtifactRows { get; private set; } = [];

    public IReadOnlyList<string> WidgetModuleKeys { get; private set; } = [];

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

        SetTitles("Import/export");
        try
        {
            var result = await _packages.ImportUploadsAsync(
                UploadInput.BundleFile,
                UploadInput.ModuleDefinitionJson,
                UploadInput.ArtifactPackages,
                CreateOptions(UploadInput),
                ct);

            StatusMessage = BuildImportStatus(result);
            return RedirectToPage("/Admin/ModulePackageImport");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or SqlException or UnauthorizedAccessException)
        {
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

        SetTitles("Import/export");
        try
        {
            var result = await _packages.ImportArtifactUploadsAsync(
                ArtifactUploadInput.ArtifactPackages,
                CreateOptions(ArtifactUploadInput),
                ct);

            StatusMessage = BuildArtifactImportStatus(result);
            return RedirectToPage("/Admin/ModulePackageImport");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or SqlException or UnauthorizedAccessException)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostImportWidgets(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Import/export");
        ValidateWidgetUpload();
        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            await using var stream = WidgetUploadInput.JsonFile!.OpenReadStream();
            var result = await _widgets.ImportAsync(stream, WidgetUploadInput.JsonFile.FileName, ct);
            StatusMessage = string.Format(
                T("Imported dashboard widgets. Created: {0}. Updated: {1}. Permission rows: {2}."),
                result.CreatedCount,
                result.UpdatedCount,
                result.PermissionRowCount);

            return RedirectToPage("/Admin/ModulePackageImport");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or SqlException)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostImportAvailable(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Import/export");
        if (string.IsNullOrWhiteSpace(ImportInput.ModuleDefinitionFileName))
        {
            ModelState.AddModelError(nameof(ImportInput.ModuleDefinitionFileName), T("Select one available module definition."));
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            var result = await _packages.ImportFromLibraryAsync(
                ImportInput.ModuleDefinitionFileName,
                CreateOptions(ImportInput),
                ct);

            StatusMessage = BuildImportStatus(result);
            return RedirectToPage("/Admin/ModulePackageImport");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or SqlException or UnauthorizedAccessException)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnGetExportArtifact(int artifactId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (artifactId <= 0)
        {
            return BadRequest(T("Artifact is required."));
        }

        try
        {
            var result = await _packages.ExportArtifactPackageAsync(artifactId, ct);
            var stream = new FileStream(
                result.PackagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 128,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);

            return File(stream, "application/zip", result.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(T(ex.Message));
        }
    }

    public async Task<IActionResult> OnGetExportWidgets(string? moduleKey, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var result = await _widgets.ExportAsync(moduleKey, ct);
            return File(result.Content, "application/json; charset=utf-8", result.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(T(ex.Message));
        }
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

        if (string.IsNullOrWhiteSpace(moduleKey))
        {
            return BadRequest(T("Module key is required."));
        }

        try
        {
            var result = await _packages.ExportModulePackageAsync(moduleKey.Trim(), includeAllVersions, ct);
            var stream = new FileStream(
                result.PackagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 128,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);

            return File(stream, "application/zip", result.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(T(ex.Message));
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var availablePackages = _packages.GetAvailablePackages();
        var definitions = await _repo.GetModuleDefinitionDocumentsAsync(ct);
        ArtifactRows = await _repo.GetArtifactsAsync(ct);
        var widgets = await _widgets.GetWidgetsAsync(null, ct);
        WidgetModuleKeys = widgets
            .Select(static row => row.ModuleKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
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

        var appliedDefinitionsByModule = AppliedDefinitions.ToDictionary(
            static row => row.ModuleKey,
            StringComparer.OrdinalIgnoreCase);
        AvailablePackages = availablePackages
            .Select(package => AvailablePackageRowModel.Create(
                package,
                appliedDefinitionsByModule.TryGetValue(package.ModuleKey, out var appliedDefinition)
                    ? appliedDefinition
                    : null,
                ArtifactRows))
            .ToList();
    }

    private string BuildImportStatus(PortableModulePackageImportResult result)
    {
        var message = string.Format(
            T("Imported module definition {0} {1}."),
            result.ModuleKey,
            result.DefinitionVersion);

        message += " " + (result.Applied
            ? T("The definition was applied.")
            : T("The definition was stored but not applied."));

        if (result.SqlRepairCount > 0)
        {
            message += " " + string.Format(
                T("Executed {0} SQL repair script(s)."),
                result.SqlRepairCount);
        }

        message += " " + string.Format(
            T("Artifacts imported or replaced: {0}. Failed: {1}."),
            result.ImportedArtifactCount,
            result.FailedArtifactCount);

        var detailRows = result.Artifacts
            .Where(static item =>
                string.Equals(item.Status, "Failed", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(item.Message))
            .Select(static item => string.IsNullOrWhiteSpace(item.Message)
                ? $"{item.FileName}: {item.Status}"
                : $"{item.FileName}: {item.Status} - {item.Message}")
            .ToArray();
        if (detailRows.Length > 0)
        {
            message += " " + T("Artifact import details:") + " " + string.Join(" | ", detailRows);
        }

        return message;
    }

    private string BuildArtifactImportStatus(IReadOnlyList<PortableModulePackageArtifactImportResult> result)
    {
        var importedCount = result.Count(static item =>
            string.Equals(item.Status, "Imported", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Status, "Replaced", StringComparison.OrdinalIgnoreCase));
        var failedCount = result.Count(static item =>
            string.Equals(item.Status, "Failed", StringComparison.OrdinalIgnoreCase));

        var message = string.Format(
            T("Artifact packages imported or replaced: {0}. Failed: {1}."),
            importedCount,
            failedCount);

        var detailRows = result
            .Where(static item =>
                string.Equals(item.Status, "Failed", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(item.Message))
            .Select(static item => string.IsNullOrWhiteSpace(item.Message)
                ? $"{item.FileName}: {item.Status}"
                : $"{item.FileName}: {item.Status} - {item.Message}")
            .ToArray();
        if (detailRows.Length > 0)
        {
            message += " " + T("Artifact import details:") + " " + string.Join(" | ", detailRows);
        }

        return message;
    }

    private void ValidateWidgetUpload()
    {
        if (WidgetUploadInput.JsonFile is null || WidgetUploadInput.JsonFile.Length == 0)
        {
            ModelState.AddModelError(nameof(WidgetUploadInput.JsonFile), T("Select one dashboard widget JSON file."));
            return;
        }

        if (!WidgetUploadInput.JsonFile.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(WidgetUploadInput.JsonFile), T("The uploaded dashboard widget file must be a .json file."));
        }

        if (WidgetUploadInput.JsonFile.Length > MaxWidgetJsonBytes)
        {
            ModelState.AddModelError(
                nameof(WidgetUploadInput.JsonFile),
                string.Format(
                    T("The uploaded dashboard widget file exceeds the configured limit of {0} bytes."),
                    MaxWidgetJsonBytes));
        }
    }

    private static PortableModulePackageImportOptions CreateOptions(UploadInputModel input)
        => new(
            input.ApplyModuleDefinition,
            input.ExecuteSqlRepairs,
            input.AllowTemporaryIncompatibleArtifacts,
            input.ReplaceExistingModuleDefinition,
            input.ReplaceExistingArtifacts,
            input.CopyConfigurationFilesFromPreviousVersion,
            input.UseArtifactsImmediately);

    private static PortableModulePackageImportOptions CreateOptions(ImportInputModel input)
        => new(
            input.ApplyModuleDefinition,
            input.ExecuteSqlRepairs,
            input.AllowTemporaryIncompatibleArtifacts,
            input.ReplaceExistingModuleDefinition,
            input.ReplaceExistingArtifacts,
            input.CopyConfigurationFilesFromPreviousVersion,
            input.UseArtifactsImmediately);

    private static PortableModulePackageImportOptions CreateOptions(ArtifactUploadInputModel input)
        => new(
            ApplyModuleDefinition: false,
            ExecuteSqlRepairs: false,
            AllowTemporaryIncompatibleArtifacts: true,
            ReplaceExistingModuleDefinition: false,
            ReplaceExistingArtifacts: input.ReplaceExistingArtifacts,
            CopyConfigurationFilesFromPreviousVersion: input.CopyConfigurationFilesFromPreviousVersion,
            UseArtifactsImmediately: input.UseArtifactsImmediately);

    public sealed class UploadInputModel
    {
        public IFormFile? BundleFile { get; set; }

        public IFormFile? ModuleDefinitionJson { get; set; }

        public List<IFormFile> ArtifactPackages { get; set; } = [];

        public bool ApplyModuleDefinition { get; set; } = true;

        public bool ExecuteSqlRepairs { get; set; } = true;

        public bool AllowTemporaryIncompatibleArtifacts { get; set; } = true;

        public bool ReplaceExistingModuleDefinition { get; set; }

        public bool ReplaceExistingArtifacts { get; set; }

        public bool CopyConfigurationFilesFromPreviousVersion { get; set; } = true;

        public bool UseArtifactsImmediately { get; set; } = true;
    }

    public sealed class ArtifactUploadInputModel
    {
        public List<IFormFile> ArtifactPackages { get; set; } = [];

        public bool ReplaceExistingArtifacts { get; set; }

        public bool CopyConfigurationFilesFromPreviousVersion { get; set; } = true;

        public bool UseArtifactsImmediately { get; set; } = true;
    }

    public sealed class WidgetUploadInputModel
    {
        public IFormFile? JsonFile { get; set; }
    }

    public sealed class ImportInputModel
    {
        public string ModuleDefinitionFileName { get; set; } = string.Empty;

        public bool ApplyModuleDefinition { get; set; } = true;

        public bool ExecuteSqlRepairs { get; set; } = true;

        public bool AllowTemporaryIncompatibleArtifacts { get; set; } = true;

        public bool ReplaceExistingModuleDefinition { get; set; }

        public bool ReplaceExistingArtifacts { get; set; }

        public bool CopyConfigurationFilesFromPreviousVersion { get; set; } = true;

        public bool UseArtifactsImmediately { get; set; } = true;
    }

}
