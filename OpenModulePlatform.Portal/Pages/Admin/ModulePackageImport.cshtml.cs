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
    public UploadInputModel UploadInput { get; set; } = new();

    [BindProperty]
    public UniversalUploadInputModel UniversalUploadInput { get; set; } = new();

    [BindProperty]
    public UniversalStagedImportInputModel UniversalStagedInput { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public UniversalExportInputModel UniversalExportInput { get; set; } = new();

    [BindProperty]
    public ExportToLibraryInputModel ExportToLibraryInput { get; set; } = new();

    [BindProperty]
    public ArtifactUploadInputModel ArtifactUploadInput { get; set; } = new();

    [BindProperty]
    public WidgetUploadInputModel WidgetUploadInput { get; set; } = new();

    [BindProperty]
    public ConfigObjectUploadInputModel HostConfigurationUploadInput { get; set; } = new();

    [BindProperty]
    public ConfigObjectUploadInputModel ConfigOverlayUploadInput { get; set; } = new();

    [BindProperty]
    public ImportInputModel ImportInput { get; set; } = new();

    [BindProperty]
    public ConfigObjectLibraryImportInputModel HostConfigurationLibraryInput { get; set; } = new();

    [BindProperty]
    public ConfigObjectLibraryImportInputModel ConfigOverlayLibraryInput { get; set; } = new();

    public IReadOnlyList<AvailablePackageRowModel> AvailablePackages { get; private set; } = [];

    public IReadOnlyList<AvailableHostConfigurationObject> AvailableHostConfigurations { get; private set; } = [];

    public IReadOnlyList<AvailableConfigOverlayObject> AvailableConfigOverlays { get; private set; } = [];

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
            ActivePanel = UploadInput.Flow switch
            {
                "bundle" => "import-bundle",
                "files" => "import-files",
                _ => "import-bundle"
            };
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
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
            ActivePanel = "import-artifacts";
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
            ActivePanel = "import-widgets";
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
            ActivePanel = "import-widgets";
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostImportHostConfiguration(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Import/export");
        if (HostConfigurationUploadInput.File is null || HostConfigurationUploadInput.File.Length == 0)
        {
            ActivePanel = "import-host-config";
            ModelState.AddModelError(nameof(HostConfigurationUploadInput.File), T("Select one host configuration JSON or zip file."));
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            var result = await _configObjects.ImportHostConfigurationUploadAsync(
                HostConfigurationUploadInput.File,
                HostConfigurationUploadInput.ReplaceExisting,
                ct);
            StatusMessage = BuildConfigObjectStatus(result);
            return RedirectToPage("/Admin/ModulePackageImport");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or SqlException or UnauthorizedAccessException)
        {
            ActivePanel = "import-host-config";
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostImportConfigOverlay(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Import/export");
        if (ConfigOverlayUploadInput.File is null || ConfigOverlayUploadInput.File.Length == 0)
        {
            ActivePanel = "import-config-overlay";
            ModelState.AddModelError(nameof(ConfigOverlayUploadInput.File), T("Select one config overlay JSON or zip file."));
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            var result = await _configObjects.ImportConfigOverlayUploadAsync(
                ConfigOverlayUploadInput.File,
                ConfigOverlayUploadInput.ReplaceExisting,
                ct);
            StatusMessage = BuildConfigObjectStatus(result);
            return RedirectToPage("/Admin/ModulePackageImport");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or SqlException or UnauthorizedAccessException)
        {
            ActivePanel = "import-config-overlay";
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
            ActivePanel = "import-library";
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
            ActivePanel = "import-library";
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostImportAvailableHostConfiguration(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Import/export");
        if (string.IsNullOrWhiteSpace(HostConfigurationLibraryInput.FileName))
        {
            ActivePanel = "import-library";
            ModelState.AddModelError(nameof(HostConfigurationLibraryInput.FileName), T("Select one available host configuration."));
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            var result = await _configObjects.ImportHostConfigurationFromLibraryAsync(
                HostConfigurationLibraryInput.FileName,
                HostConfigurationLibraryInput.ReplaceExisting,
                ct);
            StatusMessage = BuildConfigObjectStatus(result);
            return RedirectToPage("/Admin/ModulePackageImport");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or SqlException or UnauthorizedAccessException)
        {
            ActivePanel = "import-library";
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostImportAvailableConfigOverlay(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Import/export");
        if (string.IsNullOrWhiteSpace(ConfigOverlayLibraryInput.FileName))
        {
            ActivePanel = "import-library";
            ModelState.AddModelError(nameof(ConfigOverlayLibraryInput.FileName), T("Select one available config overlay."));
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            var result = await _configObjects.ImportConfigOverlayFromLibraryAsync(
                ConfigOverlayLibraryInput.FileName,
                ConfigOverlayLibraryInput.ReplaceExisting,
                ct);
            StatusMessage = BuildConfigObjectStatus(result);
            return RedirectToPage("/Admin/ModulePackageImport");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or SqlException or UnauthorizedAccessException)
        {
            ActivePanel = "import-library";
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

    public async Task<IActionResult> OnGetExportWidgets(int? widgetId, string? moduleKey, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var result = widgetId.HasValue && widgetId.Value > 0
                ? await _widgets.ExportWidgetAsync(widgetId.Value, ct)
                : await _widgets.ExportAsync(moduleKey, ct);
            return File(result.Content, "application/json; charset=utf-8", result.FileName);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(T(ex.Message));
        }
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

        var row = await _repo.GetHostConfigurationJsonAsync(documentId, ct);
        if (row is null)
        {
            return NotFound(T("Host configuration was not found."));
        }

        var fileName = $"{row.Value.HostKey}__host-config__{row.Value.ConfigurationVersion}.json";
        return File(System.Text.Encoding.UTF8.GetBytes(row.Value.Json), "application/json; charset=utf-8", fileName);
    }

    public async Task<IActionResult> OnGetExportConfigOverlay(int documentId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var row = await _repo.GetConfigOverlayJsonAsync(documentId, ct);
        if (row is null)
        {
            return NotFound(T("Config overlay was not found."));
        }

        var fileName = $"{row.Value.HostKey}__{row.Value.OverlayKey}__overlay__{row.Value.OverlayVersion}.json";
        return File(System.Text.Encoding.UTF8.GetBytes(row.Value.Json), "application/json; charset=utf-8", fileName);
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

    public async Task<IActionResult> OnPostExportModuleToLibrary(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Import/export");
        if (string.IsNullOrWhiteSpace(ExportToLibraryInput.ModuleKey))
        {
            ActivePanel = "export-module";
            ModelState.AddModelError(nameof(ExportToLibraryInput.ModuleKey), T("Module key is required."));
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            var result = await _packages.ExportModulePackageToLibraryAsync(
                ExportToLibraryInput.ModuleKey.Trim(),
                ExportToLibraryInput.IncludeAllVersions,
                ct);

            ActivePanel = "export-module";
            StatusMessage = BuildPackageLibraryExportStatus(result);
            await LoadAsync(ct);
            return Page();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ActivePanel = "export-module";
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var availablePackages = _packages.GetAvailablePackages();
        AvailableHostConfigurations = await _configObjects.GetAvailableHostConfigurationsAsync(ct);
        AvailableConfigOverlays = await _configObjects.GetAvailableConfigOverlaysAsync(ct);
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
            || string.Equals(item.Status, "Replaced", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Status, "Updated", StringComparison.OrdinalIgnoreCase));
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

    private string BuildConfigObjectStatus(ConfigObjectImportResult result)
    {
        var action = result.WasIdentical
            ? T("Skipped because the same object already exists.")
            : result.Replaced
                ? T("Replaced an existing object with the same key and version.")
                : result.Created
                    ? T("Created a new object.")
                    : T("Updated the existing object.");

        var message = string.Format(
            T("Imported {0} {1}. {2}"),
            result.ObjectKind,
            result.DisplayName,
            action);

        if (result.ConfigurationFileCount > 0)
        {
            message += " " + string.Format(
                T("Configuration files: {0}."),
                result.ConfigurationFileCount);
        }

        return message;
    }

    private string BuildPackageLibraryExportStatus(PackageLibraryExportResult result)
    {
        var message = string.Format(
            T("Exported module {0} {1} to the package library. Definition file: {2}. Artifacts exported: {3}. Skipped: {4}."),
            result.ModuleKey,
            result.DefinitionVersion,
            result.ModuleDefinitionFileName,
            result.ExportedArtifactCount,
            result.SkippedArtifactCount);

        if (result.Messages.Count > 0)
        {
            message += " " + T("Export details:") + " " + string.Join(" | ", result.Messages);
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

    public sealed class UploadInputModel
    {
        public string Flow { get; set; } = string.Empty;

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

    public sealed class ExportToLibraryInputModel
    {
        public string ModuleKey { get; set; } = string.Empty;

        public bool IncludeAllVersions { get; set; }
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

    public sealed class ConfigObjectUploadInputModel
    {
        public IFormFile? File { get; set; }

        public bool ReplaceExisting { get; set; }
    }

    public sealed class ConfigObjectLibraryImportInputModel
    {
        public string FileName { get; set; } = string.Empty;

        public bool ReplaceExisting { get; set; }
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
