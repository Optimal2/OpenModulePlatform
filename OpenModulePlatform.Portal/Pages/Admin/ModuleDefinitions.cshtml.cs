// File: OpenModulePlatform.Portal/Pages/Admin/ModuleDefinitions.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Lists versioned module definition documents.
/// </summary>
public sealed class ModuleDefinitionsModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;
    private readonly PortableModulePackageService _packages;

    public ModuleDefinitionsModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo,
        PortableModulePackageService packages)
        : base(options, rbac)
    {
        _repo = repo;
        _packages = packages;
    }

    public IReadOnlyList<ModuleDefinitionDocumentRow> Rows { get; private set; } = [];

    public IReadOnlyList<ModuleDefinitionIntegritySummaryRow> IntegrityRows { get; private set; } = [];

    public IReadOnlyList<AvailablePackageRowModel> AvailablePackages { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Module definition versions");
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostImportAvailable(string moduleDefinitionFileName, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (string.IsNullOrWhiteSpace(moduleDefinitionFileName))
        {
            ModelState.AddModelError(string.Empty, T("Select one available module definition."));
            SetTitles("Module definition versions");
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            var result = await _packages.ImportFromLibraryAsync(
                moduleDefinitionFileName,
                CreateDefaultImportOptions(),
                ct);

            StatusMessage = BuildImportStatus(result);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or SqlException or UnauthorizedAccessException)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            SetTitles("Module definition versions");
            await LoadAsync(ct);
            return Page();
        }

        return RedirectToPage("/Admin/ModuleDefinitions");
    }

    public async Task<IActionResult> OnPostApplyAndRepair(int moduleDefinitionDocumentId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var applyResult = await _repo.ApplyModuleDefinitionDocumentAsync(
                moduleDefinitionDocumentId,
                allowTemporaryIncompatibleArtifacts: true,
                ct);

            if (!applyResult.Applied)
            {
                StatusMessage = T("Module definition was not applied because current artifact selections are incompatible.");
                return RedirectToPage("/Admin/ModuleDefinitions");
            }

            var repairResult = await _repo.ExecuteModuleDefinitionSqlRepairsAsync(
                moduleDefinitionDocumentId,
                ct);
            StatusMessage = repairResult.ExecutedCount == 0
                ? T("Module definition applied. No executable SQL repairs were needed.")
                : string.Format(
                    T("Module definition applied. Executed {0} SQL repair script(s)."),
                    repairResult.ExecutedCount);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            SetTitles("Module definition versions");
            await LoadAsync(ct);
            return Page();
        }

        return RedirectToPage("/Admin/ModuleDefinitions");
    }

    public async Task<IActionResult> OnPostRepairSql(int moduleDefinitionDocumentId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var result = await _repo.ExecuteModuleDefinitionSqlRepairsAsync(moduleDefinitionDocumentId, ct);
            StatusMessage = result.ExecutedCount == 0
                ? T("No executable module definition SQL repairs were needed.")
                : string.Format(
                    T("Executed {0} SQL repair script(s)."),
                    result.ExecutedCount);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            SetTitles("Module definition versions");
            await LoadAsync(ct);
            return Page();
        }

        return RedirectToPage("/Admin/ModuleDefinitions");
    }

    public async Task<IActionResult> OnPostRepairAllSql(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var result = await _repo.ExecuteAppliedModuleDefinitionSqlRepairsAsync(ct);
            StatusMessage = result.ExecutedCount == 0
                ? T("No executable module definition SQL repairs were needed.")
                : string.Format(
                    T("Executed {0} module definition SQL repair scripts across active module definitions."),
                    result.ExecutedCount);
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(string.Empty, T($"The module definition SQL repair failed: {ex.Message}"));
            SetTitles("Module definition versions");
            await LoadAsync(ct);
            return Page();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            SetTitles("Module definition versions");
            await LoadAsync(ct);
            return Page();
        }

        return RedirectToPage("/Admin/ModuleDefinitions");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await _repo.GetModuleDefinitionDocumentsAsync(ct);
        IntegrityRows = await _repo.GetModuleDefinitionIntegritySummariesAsync(ct);

        var appliedDefinitionsByModule = Rows
            .Where(static row => row.IsApplied)
            .GroupBy(static row => row.ModuleKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderByDescending(row => row.AppliedUtc)
                .ThenByDescending(row => row.UpdatedUtc)
                .First())
            .ToDictionary(static row => row.ModuleKey, StringComparer.OrdinalIgnoreCase);
        var installedArtifacts = await _repo.GetArtifactsAsync(ct);
        AvailablePackages = _packages.GetAvailablePackages()
            .Select(package => AvailablePackageRowModel.Create(
                package,
                appliedDefinitionsByModule.TryGetValue(package.ModuleKey, out var appliedDefinition)
                    ? appliedDefinition
                    : null,
                installedArtifacts))
            .OrderBy(static row => row.InstallState == AvailablePackageInstallState.SameVersion)
            .ThenBy(static row => row.ModuleKey, StringComparer.OrdinalIgnoreCase)
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

    private static PortableModulePackageImportOptions CreateDefaultImportOptions()
        => new(
            ApplyModuleDefinition: true,
            ExecuteSqlRepairs: true,
            AllowTemporaryIncompatibleArtifacts: true,
            ReplaceExistingModuleDefinition: false,
            ReplaceExistingArtifacts: false,
            CopyConfigurationFilesFromPreviousVersion: true,
            UseArtifactsImmediately: true);
}
