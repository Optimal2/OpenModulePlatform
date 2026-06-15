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
    private const int MaxStatusMessageLength = 1800;

    private readonly OmpAdminRepository _repo;
    private readonly PortableModulePackageService _packages;
    private readonly PortalDashboardWidgetPackageService _widgets;
    private readonly ConfigOverlayObjectService _configObjects;
    private readonly PortalDeploymentLockService _deploymentLocks;

    public ModulePackageImportModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo,
        PortableModulePackageService packages,
        PortalDashboardWidgetPackageService widgets,
        ConfigOverlayObjectService configObjects,
        PortalDeploymentLockService deploymentLocks)
        : base(options, rbac)
    {
        _repo = repo;
        _packages = packages;
        _widgets = widgets;
        _configObjects = configObjects;
        _deploymentLocks = deploymentLocks;
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

    public string ActivePanel { get; private set; } = "import-universal";

    [TempData]
    public string? StatusMessage { get; set; }

    public string? InlineStatusMessage { get; private set; }

    public IReadOnlyList<UniversalImportResultViewModel> UniversalImportResults { get; private set; } = [];

    public int UniversalImportDetailCount => UniversalImportResults.Sum(static result => result.Items.Count);

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
            var files = GetSelectedPackageFiles();
            if (files.Count == 0)
            {
                throw new InvalidOperationException(T("Select at least one universal module package zip."));
            }

            await using var deploymentLock = await _deploymentLocks.AcquireUniversalImportLockAsync(
                User.Identity?.Name,
                ct);

            var results = new List<UniversalPackageImportResult>(files.Count);
            foreach (var file in files)
            {
                var result = await _packages.ImportUniversalPackageUploadAsync(
                    file,
                    CreateOptions(UniversalUploadInput),
                    !UniversalUploadInput.QuickImport && UniversalUploadInput.ReplaceExistingConfigObjects,
                    ct);
                results.Add(result);
            }

            return await ShowUniversalImportResultsAsync(results, ct);
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
            var files = GetSelectedPackageFiles();
            if (files.Count == 0)
            {
                throw new InvalidOperationException(T("Select one universal module package zip to preview."));
            }

            if (files.Count > 1)
            {
                throw new InvalidOperationException(T("Preview supports one universal module package at a time. Use Import all to import multiple packages."));
            }

            UniversalPreview = await _packages.StageUniversalPackageUploadForPreviewAsync(
                files[0],
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
            await using var deploymentLock = await _deploymentLocks.AcquireUniversalImportLockAsync(
                User.Identity?.Name,
                ct);

            var result = await _packages.ImportStagedUniversalPackageAsync(
                UniversalStagedInput.Token,
                UniversalStagedInput.SelectedItemPaths,
                CreateOptions(UniversalStagedInput),
                !UniversalStagedInput.QuickImport && UniversalStagedInput.ReplaceExistingConfigObjects,
                ct);

            return await ShowUniversalImportResultsAsync([result], ct);
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
            Response.OnCompleted(DeleteTemporaryExportFileOnCompleted, result.PackagePath);

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

    private async Task<IActionResult> ShowUniversalImportResultsAsync(
        IReadOnlyList<UniversalPackageImportResult> results,
        CancellationToken ct)
    {
        InlineStatusMessage = BuildUniversalImportSummary(results);
        UniversalImportResults = BuildUniversalImportResultViewModels(results);
        ActivePanel = "import-universal";
        await LoadAsync(ct);
        return Page();
    }

    private string BuildUniversalImportSummary(UniversalPackageImportResult result)
    {
        var packageName = GetUniversalPackageName(result);
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

        return BoundStatusMessage(message);
    }

    private string BuildUniversalImportSummary(IReadOnlyList<UniversalPackageImportResult> results)
    {
        if (results.Count == 1)
        {
            return BuildUniversalImportSummary(results[0]);
        }

        var message = string.Format(
            T("Imported {0} universal module packages. Imported/updated: {1}. Skipped: {2}. Failed: {3}."),
            results.Count,
            results.Sum(static result => result.ImportedCount),
            results.Sum(static result => result.SkippedCount),
            results.Sum(static result => result.FailedCount));

        return BoundStatusMessage(message);
    }

    private static IReadOnlyList<UniversalImportResultViewModel> BuildUniversalImportResultViewModels(
        IReadOnlyList<UniversalPackageImportResult> results)
    {
        return results
            .Select(result => new UniversalImportResultViewModel(
                GetUniversalPackageName(result),
                result.ImportedCount,
                result.SkippedCount,
                result.FailedCount,
                result.TargetHostProfile,
                result.Items
                    .Select(item => new UniversalImportItemViewModel(
                        item.Kind,
                        item.Path,
                        item.Status,
                        item.Message))
                    .ToArray()))
            .ToArray();
    }

    private static string GetUniversalPackageName(UniversalPackageImportResult result)
        => string.IsNullOrWhiteSpace(result.PackageKey)
            ? result.SourceName
            : $"{result.PackageKey} {result.PackageVersion}".Trim();

    private string BoundStatusMessage(string message)
    {
        if (message.Length <= MaxStatusMessageLength)
        {
            return message;
        }

        var suffix = " " + T("The import result was shortened to avoid an oversized browser cookie.");
        var availableLength = Math.Max(0, MaxStatusMessageLength - suffix.Length);
        return message[..availableLength] + suffix;
    }

    private IReadOnlyList<IFormFile> GetSelectedPackageFiles()
        => UniversalUploadInput.PackageFiles
            .Where(static file => file.Length > 0)
            .ToArray();

    private static PortableModulePackageImportOptions CreateOptions(UniversalUploadInputModel input)
        => NormalizeQuickImportOptions(new(
            input.ApplyModuleDefinitions,
            input.ExecuteSqlRepairs,
            input.AllowTemporaryIncompatibleArtifacts,
            input.ReplaceExistingModuleDefinitions,
            input.ReplaceExistingArtifacts,
            input.ReplaceExistingDashboardWidgets,
            input.CopyConfigurationFilesFromPreviousVersion,
            input.UseArtifactsImmediately,
            input.QuickImport));

    private static PortableModulePackageImportOptions CreateOptions(UniversalStagedImportInputModel input)
        => NormalizeQuickImportOptions(new(
            input.ApplyModuleDefinitions,
            input.ExecuteSqlRepairs,
            input.AllowTemporaryIncompatibleArtifacts,
            input.ReplaceExistingModuleDefinitions,
            input.ReplaceExistingArtifacts,
            input.ReplaceExistingDashboardWidgets,
            input.CopyConfigurationFilesFromPreviousVersion,
            input.UseArtifactsImmediately,
            input.QuickImport));

    private static PortableModulePackageImportOptions NormalizeQuickImportOptions(PortableModulePackageImportOptions options)
        => options.QuickImport
            ? options with
            {
                // The service option is singular because one module definition document is imported per package item.
                ReplaceExistingModuleDefinition = false,
                ReplaceExistingArtifacts = false,
                ReplaceExistingDashboardWidgets = false
            }
            : options;

    private static Task DeleteTemporaryExportFileOnCompleted(object state)
    {
        TryDeleteTemporaryFile((string)state);
        return Task.CompletedTask;
    }

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
        public List<IFormFile> PackageFiles { get; set; } = [];

        public bool QuickImport { get; set; } = true;

        public bool ApplyModuleDefinitions { get; set; } = true;

        public bool ExecuteSqlRepairs { get; set; } = true;

        public bool AllowTemporaryIncompatibleArtifacts { get; set; } = true;

        public bool ReplaceExistingModuleDefinitions { get; set; }

        public bool ReplaceExistingArtifacts { get; set; }

        public bool ReplaceExistingDashboardWidgets { get; set; }

        public bool ReplaceExistingConfigObjects { get; set; }

        public bool CopyConfigurationFilesFromPreviousVersion { get; set; } = true;

        public bool UseArtifactsImmediately { get; set; } = true;
    }

    public sealed record UniversalImportResultViewModel(
        string PackageName,
        int ImportedCount,
        int SkippedCount,
        int FailedCount,
        string? TargetHostProfile,
        IReadOnlyList<UniversalImportItemViewModel> Items);

    public sealed record UniversalImportItemViewModel(
        string Kind,
        string Path,
        string Status,
        string? Message);

    public sealed class UniversalStagedImportInputModel
    {
        public string Token { get; set; } = string.Empty;

        public List<string> SelectedItemPaths { get; set; } = [];

        public bool QuickImport { get; set; } = true;

        public bool ApplyModuleDefinitions { get; set; } = true;

        public bool ExecuteSqlRepairs { get; set; } = true;

        public bool AllowTemporaryIncompatibleArtifacts { get; set; } = true;

        public bool ReplaceExistingModuleDefinitions { get; set; }

        public bool ReplaceExistingArtifacts { get; set; }

        public bool ReplaceExistingDashboardWidgets { get; set; }

        public bool ReplaceExistingConfigObjects { get; set; }

        public bool CopyConfigurationFilesFromPreviousVersion { get; set; } = true;

        public bool UseArtifactsImmediately { get; set; } = true;

        public static UniversalStagedImportInputModel From(
            UniversalUploadInputModel input,
            string token)
            => new()
            {
                Token = token,
                QuickImport = input.QuickImport,
                ApplyModuleDefinitions = input.ApplyModuleDefinitions,
                ExecuteSqlRepairs = input.ExecuteSqlRepairs,
                AllowTemporaryIncompatibleArtifacts = input.AllowTemporaryIncompatibleArtifacts,
                ReplaceExistingModuleDefinitions = input.ReplaceExistingModuleDefinitions,
                ReplaceExistingArtifacts = input.ReplaceExistingArtifacts,
                ReplaceExistingDashboardWidgets = input.ReplaceExistingDashboardWidgets,
                ReplaceExistingConfigObjects = input.ReplaceExistingConfigObjects,
                CopyConfigurationFilesFromPreviousVersion = input.CopyConfigurationFilesFromPreviousVersion,
                UseArtifactsImmediately = input.UseArtifactsImmediately
            };
    }

    public sealed class UniversalExportInputModel
    {
        public string PackageKey { get; set; } = "omp-universal";

        public string PackageVersion { get; set; } = CreateDefaultPackageVersion();

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

        private static string CreateDefaultPackageVersion()
            => DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
    }

}
