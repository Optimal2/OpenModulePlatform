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
/// Imports and exports portable module packages containing a module definition
/// and the related artifact package zips that HostAgent can deploy.
/// </summary>
public sealed class ModulePackageImportModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;
    private readonly PortableModulePackageService _packages;

    public ModulePackageImportModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo,
        PortableModulePackageService packages)
        : base(options, rbac)
    {
        _repo = repo;
        _packages = packages;
    }

    [BindProperty]
    public UploadInputModel UploadInput { get; set; } = new();

    [BindProperty]
    public ImportInputModel ImportInput { get; set; } = new();

    public IReadOnlyList<AvailablePackageRowModel> AvailablePackages { get; private set; } = [];

    public IReadOnlyList<ModuleDefinitionDocumentRow> AppliedDefinitions { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Module packages");
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

        SetTitles("Module packages");
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

    public async Task<IActionResult> OnPostImportAvailable(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Module packages");
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
        var installedArtifacts = await _repo.GetArtifactsAsync(ct);
        AvailablePackages = availablePackages
            .Select(package => AvailablePackageRowModel.Create(
                package,
                appliedDefinitionsByModule.TryGetValue(package.ModuleKey, out var appliedDefinition)
                    ? appliedDefinition
                    : null,
                installedArtifacts))
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

        return message;
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

    public sealed class AvailablePackageRowModel
    {
        public required string ModuleKey { get; init; }

        public required string DefinitionVersion { get; init; }

        public required string ModuleDefinitionFileName { get; init; }

        public required IReadOnlyList<ArtifactPackageFile> ArtifactFiles { get; init; }

        public string? InstalledDefinitionVersion { get; init; }

        public int SameArtifactVersionCount { get; init; }

        public int OtherArtifactVersionCount { get; init; }

        public int MissingArtifactCount { get; init; }

        public AvailablePackageInstallState InstallState { get; init; }

        public string StatusLabel => InstallState switch
        {
            AvailablePackageInstallState.SameVersion => "Installed same version",
            AvailablePackageInstallState.OtherVersion => "Installed other version",
            AvailablePackageInstallState.Partial => "Partially installed",
            _ => "Not installed"
        };

        public string StatusClass => InstallState switch
        {
            AvailablePackageInstallState.SameVersion => "integrity-pill--ok",
            AvailablePackageInstallState.OtherVersion => "integrity-pill--warning",
            AvailablePackageInstallState.Partial => "integrity-pill--warning",
            _ => "integrity-pill--neutral"
        };

        public string ImportGuidance => InstallState switch
        {
            AvailablePackageInstallState.SameVersion =>
                "Importing again should keep unchanged content and skip artifact packages that already exist with identical content.",
            AvailablePackageInstallState.OtherVersion =>
                "Importing will apply this module definition version and can move matching app instances to the package artifact versions.",
            AvailablePackageInstallState.Partial =>
                "Importing will fill in missing pieces and may update desired artifact versions for matching app instances.",
            _ =>
                "Importing will add this module definition and register the package artifacts so HostAgent can deploy them."
        };

        public static AvailablePackageRowModel Create(
            AvailablePortableModulePackage package,
            ModuleDefinitionDocumentRow? appliedDefinition,
            IReadOnlyList<ArtifactRow> installedArtifacts)
        {
            var installedDefinitionVersion = appliedDefinition?.DefinitionVersion;
            var definitionSame = string.Equals(
                installedDefinitionVersion,
                package.DefinitionVersion,
                StringComparison.OrdinalIgnoreCase);
            var definitionInstalled = !string.IsNullOrWhiteSpace(installedDefinitionVersion);
            var installedArtifactsBySlot = installedArtifacts.ToLookup(
                artifact => BuildArtifactSlotKey(
                    artifact.ModuleKey,
                    artifact.AppKey,
                    artifact.PackageType,
                    artifact.TargetName),
                StringComparer.OrdinalIgnoreCase);

            var sameArtifacts = 0;
            var otherArtifacts = 0;
            var missingArtifacts = 0;
            foreach (var file in package.ArtifactFiles)
            {
                var matches = installedArtifactsBySlot[
                    BuildArtifactSlotKey(file.ModuleKey, file.AppKey, file.PackageType, file.TargetName)];

                if (matches.Any(artifact => artifact.Version.Equals(file.Version, StringComparison.OrdinalIgnoreCase)))
                {
                    sameArtifacts++;
                }
                else if (matches.Any())
                {
                    otherArtifacts++;
                }
                else
                {
                    missingArtifacts++;
                }
            }

            var allArtifactsSame = package.ArtifactFiles.Count == sameArtifacts;
            var noArtifactsInstalled = sameArtifacts == 0 && otherArtifacts == 0;
            var installState = (definitionInstalled, definitionSame, allArtifactsSame, noArtifactsInstalled) switch
            {
                (true, true, true, _) => AvailablePackageInstallState.SameVersion,
                (false, _, _, true) => AvailablePackageInstallState.NotInstalled,
                (true, false, _, _) => AvailablePackageInstallState.OtherVersion,
                _ => AvailablePackageInstallState.Partial
            };

            return new AvailablePackageRowModel
            {
                ModuleKey = package.ModuleKey,
                DefinitionVersion = package.DefinitionVersion,
                ModuleDefinitionFileName = package.ModuleDefinitionFileName,
                ArtifactFiles = package.ArtifactFiles,
                InstalledDefinitionVersion = installedDefinitionVersion,
                SameArtifactVersionCount = sameArtifacts,
                OtherArtifactVersionCount = otherArtifacts,
                MissingArtifactCount = missingArtifacts,
                InstallState = installState
            };
        }

        private static string BuildArtifactSlotKey(
            string moduleKey,
            string appKey,
            string packageType,
            string? targetName)
            => string.Join('\u001f', moduleKey, appKey, packageType, targetName ?? string.Empty);
    }

    public enum AvailablePackageInstallState
    {
        NotInstalled,
        SameVersion,
        OtherVersion,
        Partial
    }
}
