// File: OpenModulePlatform.Portal/Pages/Admin/ModuleDefinitionUpload.cshtml.cs
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Uploads one module definition JSON document into OMP.
/// </summary>
public sealed class ModuleDefinitionUploadModel : OmpPortalPageModel
{
    private const int MaxDefinitionBytes = 1024 * 1024 * 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private readonly OmpAdminRepository _repo;

    public ModuleDefinitionUploadModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Upload module definition");
        return Page();
    }

    public async Task<IActionResult> OnPost(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Upload module definition");
        ValidateInput();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var definition = await ReadDefinitionAsync(Input.JsonFile!, ct);
            var saveResult = await _repo.SaveModuleDefinitionDocumentAsync(
                definition,
                Input.ReplaceExistingDefinition,
                ct);

            if (!Input.ApplyImmediately)
            {
                StatusMessage = saveResult.WasIdentical
                    ? T("The same module definition already exists.")
                    : T("Module definition uploaded.");

                return RedirectToPage("/Admin/ModuleDefinitionEdit", new { id = saveResult.ModuleDefinitionDocumentId });
            }

            var applyResult = await _repo.ApplyModuleDefinitionDocumentAsync(
                saveResult.ModuleDefinitionDocumentId,
                Input.AllowTemporaryIncompatibleArtifacts,
                ct);

            if (!applyResult.Applied)
            {
                StatusMessage = T("Module definition uploaded but not applied because current artifact selections are incompatible. Review and confirm temporary incompatibility if this is intentional.");
                return RedirectToPage("/Admin/ModuleDefinitionEdit", new { id = saveResult.ModuleDefinitionDocumentId });
            }

            StatusMessage = applyResult.IncompatibleReferences.Count == 0
                ? T("Module definition uploaded and applied.")
                : string.Format(
                    T("Module definition uploaded and applied with {0} temporarily incompatible artifact references."),
                    applyResult.IncompatibleReferences.Count);

            return RedirectToPage("/Admin/ModuleDefinitionEdit", new { id = saveResult.ModuleDefinitionDocumentId });
        }
        catch (JsonException ex)
        {
            ModelState.AddModelError(nameof(Input.JsonFile), T($"The uploaded JSON could not be read: {ex.Message}"));
            return Page();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            return Page();
        }
        catch (IOException ex)
        {
            ModelState.AddModelError(nameof(Input.JsonFile), T($"The uploaded file could not be read: {ex.Message}"));
            return Page();
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(string.Empty, T($"The module definition could not be saved: {ex.Message}"));
            return Page();
        }
    }

    private void ValidateInput()
    {
        if (Input.JsonFile is null || Input.JsonFile.Length == 0)
        {
            ModelState.AddModelError(nameof(Input.JsonFile), T("Select one module definition JSON file."));
            return;
        }

        if (!Input.JsonFile.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(Input.JsonFile), T("The uploaded module definition must be a .json file."));
        }

        if (Input.JsonFile.Length > MaxDefinitionBytes)
        {
            ModelState.AddModelError(
                nameof(Input.JsonFile),
                T($"The uploaded module definition exceeds the configured limit of {MaxDefinitionBytes} bytes."));
        }
    }

    private static async Task<ModuleDefinitionDocumentEditData> ReadDefinitionAsync(
        IFormFile file,
        CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var jsonText = await reader.ReadToEndAsync(ct);
        var root = JsonNode.Parse(jsonText)
            ?? throw new InvalidOperationException("The module definition JSON file is empty.");

        var moduleKey = GetJsonStringProperty(root, "moduleKey");
        var definitionVersion = GetJsonStringProperty(root, "definitionVersion");
        if (string.IsNullOrWhiteSpace(moduleKey) || string.IsNullOrWhiteSpace(definitionVersion))
        {
            throw new InvalidOperationException("Module definition JSON must contain moduleKey and definitionVersion.");
        }

        var normalizedJson = root.ToJsonString(JsonOptions);
        var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedJson))).ToLowerInvariant();

        return new ModuleDefinitionDocumentEditData
        {
            ModuleKey = moduleKey,
            DefinitionVersion = definitionVersion,
            FormatVersion = GetJsonIntProperty(root, "formatVersion", 1),
            DefinitionJson = normalizedJson,
            DefinitionSha256 = sha256,
            SourceName = Truncate(Path.GetFileName(file.FileName), 400),
            CompatibleArtifacts = ReadCompatibleArtifacts(root)
        };
    }

    private static IReadOnlyList<ModuleDefinitionCompatibilityEditData> ReadCompatibleArtifacts(JsonNode root)
    {
        if (GetJsonObjectProperty(root, "compatibleArtifacts") is not JsonArray items)
        {
            return [];
        }

        var entries = new List<ModuleDefinitionCompatibilityEditData>();
        foreach (var item in items)
        {
            if (item is not JsonObject)
            {
                continue;
            }

            var appKey = GetJsonStringProperty(item, "appKey");
            var packageType = GetJsonStringProperty(item, "packageType");
            if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(packageType))
            {
                throw new InvalidOperationException("Each compatibleArtifacts item must contain appKey and packageType.");
            }

            entries.Add(
                new ModuleDefinitionCompatibilityEditData
                {
                    AppKey = appKey,
                    PackageType = packageType,
                    TargetName = NullIfWhiteSpace(GetJsonStringProperty(item, "targetName")),
                    RelativePathTemplate = NullIfWhiteSpace(GetJsonStringProperty(item, "relativePathTemplate")),
                    MinArtifactVersion = NullIfWhiteSpace(GetJsonStringProperty(item, "minVersion")),
                    MaxArtifactVersion = NullIfWhiteSpace(GetJsonStringProperty(item, "maxVersion"))
                });
        }

        return entries;
    }

    private static JsonNode? GetJsonObjectProperty(JsonNode? node, string propertyName)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        return obj.FirstOrDefault(property => property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string GetJsonStringProperty(JsonNode? node, string propertyName)
    {
        var value = GetJsonObjectProperty(node, propertyName);
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? text.Trim()
            : string.Empty;
    }

    private static int GetJsonIntProperty(JsonNode? node, string propertyName, int defaultValue)
    {
        var value = GetJsonObjectProperty(node, propertyName);
        if (value is not JsonValue jsonValue)
        {
            return defaultValue;
        }

        if (jsonValue.TryGetValue<int>(out var number))
        {
            return number;
        }

        return jsonValue.TryGetValue<string>(out var text)
            && int.TryParse(text, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    public sealed class InputModel
    {
        [Display(Name = "Module definition JSON")]
        public IFormFile? JsonFile { get; set; }

        [Display(Name = "Apply immediately")]
        public bool ApplyImmediately { get; set; } = true;

        [Display(Name = "Allow temporary incompatible artifact selections")]
        public bool AllowTemporaryIncompatibleArtifacts { get; set; }

        [Display(Name = "Replace existing definition with same module key and version")]
        public bool ReplaceExistingDefinition { get; set; }
    }
}
