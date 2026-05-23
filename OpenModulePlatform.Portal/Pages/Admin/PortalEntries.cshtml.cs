// File: OpenModulePlatform.Portal/Pages/Admin/PortalEntries.cshtml.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class PortalEntriesModel : OmpPortalPageModel
{
    private readonly PortalEntryService _portalEntries;

    public PortalEntriesModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        PortalEntryService portalEntries)
        : base(options, rbac)
    {
        _portalEntries = portalEntries;
    }

    public InputModel Input { get; set; } = new();

    public EditInputModel EditInput { get; set; } = new();

    [BindProperty]
    public List<LayoutEntryInput> LayoutEntries { get; set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public IReadOnlyList<PortalEntryAdminRow> Rows { get; private set; } = [];

    public IReadOnlyDictionary<int, int> RowDepths { get; private set; } = new Dictionary<int, int>();

    public IReadOnlyList<OptionItem> ParentOptions { get; private set; } = [];

    public bool OpenCreatePanel { get; private set; }

    public int? OpenEditorPortalEntryId { get; private set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles("Navigation");
        Input.IsEnabled = true;
        return Page();
    }

    public async Task<IActionResult> OnPostCreate([Bind(Prefix = nameof(Input))] InputModel input, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        Input = input;
        OpenCreatePanel = true;
        await LoadAsync(ct);
        SetTitles("Navigation");
        ValidateCreateInput();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        await _portalEntries.CreateAsync(
            new PortalEntryCreateData
            {
                DisplayName = Input.DisplayName.Trim(),
                ParentEntryId = Input.ParentEntryId,
                Description = Clean(Input.Description),
                LogoUrl = Clean(Input.LogoUrl),
                IconKey = Clean(Input.IconKey),
                TargetUrl = Clean(Input.TargetUrl),
                TargetEntryKey = Clean(Input.TargetEntryKey),
                IsEnabled = Input.IsEnabled,
                DefaultSortOrder = Input.DefaultSortOrder
            },
            ct);

        StatusMessage = T("Custom link created.");
        return RedirectToPage("/Admin/PortalEntries");
    }

    public async Task<IActionResult> OnPostUpdate([Bind(Prefix = nameof(EditInput))] EditInputModel input, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        EditInput = input;
        OpenEditorPortalEntryId = input.PortalEntryId;
        await LoadAsync(ct);
        SetTitles("Navigation");

        var existing = Rows.FirstOrDefault(row => row.PortalEntryId == input.PortalEntryId);
        if (existing is null)
        {
            return NotFound();
        }

        ValidateEditInput(existing);
        if (await _portalEntries.WouldCreateCycleAsync(EditInput.PortalEntryId, EditInput.ParentEntryId, ct))
        {
            ModelState.AddModelError($"{nameof(EditInput)}.{nameof(EditInput.ParentEntryId)}", T("The selected parent would create a navigation cycle."));
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var updated = await _portalEntries.UpdateAsync(
            new PortalEntryEditData
            {
                PortalEntryId = EditInput.PortalEntryId,
                ParentEntryId = EditInput.ParentEntryId,
                DisplayName = EditInput.DisplayName.Trim(),
                Description = Clean(EditInput.Description),
                LogoUrl = Clean(EditInput.LogoUrl),
                IconKey = Clean(EditInput.IconKey),
                TargetUrl = Clean(EditInput.TargetUrl),
                TargetEntryKey = Clean(EditInput.TargetEntryKey),
                IsEnabled = EditInput.IsEnabled,
                DefaultSortOrder = EditInput.DefaultSortOrder
            },
            ct);

        if (!updated)
        {
            return NotFound();
        }

        StatusMessage = T("Navigation entry updated.");
        return RedirectToPage("/Admin/PortalEntries");
    }

    public async Task<IActionResult> OnPostLayout(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        RemoveModelStatePrefix(nameof(Input));
        RemoveModelStatePrefix(nameof(EditInput));

        if (LayoutEntries.Count == 0)
        {
            ModelState.AddModelError(string.Empty, T("No navigation entries were submitted."));
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            SetTitles("Navigation");
            return Page();
        }

        try
        {
            await _portalEntries.UpdateLayoutAsync(
                LayoutEntries.Select(entry => new PortalEntryLayoutUpdate
                {
                    PortalEntryId = entry.PortalEntryId,
                    ParentEntryId = entry.ParentEntryId,
                    IsEnabled = entry.IsEnabled,
                    SortOrder = entry.SortOrder
                }).ToArray(),
                ct);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            await LoadAsync(ct);
            SetTitles("Navigation");
            return Page();
        }

        StatusMessage = T("All modules saved.");
        return RedirectToPage("/Admin/PortalEntries");
    }

    public async Task<IActionResult> OnPostDelete(int portalEntryId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var deleted = await _portalEntries.DeleteAsync(portalEntryId, ct);
        StatusMessage = deleted
            ? T("Navigation entry removed.")
            : T("Navigation entry was not found.");
        return RedirectToPage("/Admin/PortalEntries");
    }

    public int GetDepth(PortalEntryAdminRow row)
        => RowDepths.TryGetValue(row.PortalEntryId, out var depth) ? depth : 0;

    private static IReadOnlyDictionary<int, int> BuildDepthLookup(IReadOnlyList<PortalEntryAdminRow> rows)
    {
        var rowsById = rows.ToDictionary(item => item.PortalEntryId);
        return rows.ToDictionary(row => row.PortalEntryId, row => CalculateDepth(row, rowsById));
    }

    private static int CalculateDepth(
        PortalEntryAdminRow row,
        IReadOnlyDictionary<int, PortalEntryAdminRow> rowsById)
    {
        var visited = new HashSet<int> { row.PortalEntryId };
        var depth = 0;
        var parentId = row.ParentEntryId;

        while (parentId.HasValue && rowsById.TryGetValue(parentId.Value, out var parent) && visited.Add(parent.PortalEntryId))
        {
            depth++;
            parentId = parent.ParentEntryId;
        }

        return Math.Min(depth, 3);
    }

    private void RemoveModelStatePrefix(string prefix)
    {
        var prefixWithDot = prefix + ".";
        foreach (var key in ModelState.Keys.Where(key => key.StartsWith(prefixWithDot, StringComparison.Ordinal)).ToArray())
        {
            ModelState.Remove(key);
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await _portalEntries.GetAdminRowsAsync(ct);
        RowDepths = BuildDepthLookup(Rows);
        ParentOptions = await _portalEntries.GetParentOptionsAsync(null, ct);
        LayoutEntries = Rows
            .Select((row, index) => new LayoutEntryInput
            {
                PortalEntryId = row.PortalEntryId,
                ParentEntryId = row.ParentEntryId,
                IsEnabled = row.IsEnabled,
                SortOrder = row.DefaultSortOrder == 0 ? (index + 1) * 10 : row.DefaultSortOrder
            })
            .ToList();
    }

    private void ValidateCreateInput()
    {
        Input.DisplayName = Input.DisplayName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(Input.TargetUrl) && string.IsNullOrWhiteSpace(Input.TargetEntryKey))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.TargetUrl)}", T("Enter a target URL or target entry key."));
        }

        if (!string.IsNullOrWhiteSpace(Input.TargetUrl) && !IsSafeTargetUrl(Input.TargetUrl))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.TargetUrl)}", T("Use an absolute http/https URL, a local path starting with /, or a relative path."));
        }

        if (Input.ParentEntryId is int parentEntryId
            && !ParentOptions.Any(option => string.Equals(option.Value, parentEntryId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.ParentEntryId)}", T("Select a valid parent entry."));
        }
    }

    private void ValidateEditInput(PortalEntryAdminRow existing)
    {
        EditInput.DisplayName = EditInput.DisplayName?.Trim() ?? string.Empty;

        if (!existing.SourceAppInstanceId.HasValue
            && string.IsNullOrWhiteSpace(EditInput.TargetUrl)
            && string.IsNullOrWhiteSpace(EditInput.TargetEntryKey))
        {
            ModelState.AddModelError($"{nameof(EditInput)}.{nameof(EditInput.TargetUrl)}", T("Enter a target URL or target entry key."));
        }

        if (!string.IsNullOrWhiteSpace(EditInput.TargetUrl) && !IsSafeTargetUrl(EditInput.TargetUrl))
        {
            ModelState.AddModelError($"{nameof(EditInput)}.{nameof(EditInput.TargetUrl)}", T("Use an absolute http/https URL, a local path starting with /, or a relative path."));
        }

        if (EditInput.ParentEntryId is int parentEntryId
            && !ParentOptions.Any(option => string.Equals(option.Value, parentEntryId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)))
        {
            ModelState.AddModelError($"{nameof(EditInput)}.{nameof(EditInput.ParentEntryId)}", T("Select a valid parent entry."));
        }
    }

    private static bool IsSafeTargetUrl(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.Scheme is "http" or "https";
        }

        return true;
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public sealed class InputModel
    {
        [Required]
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [Display(Name = "Parent entry")]
        public int? ParentEntryId { get; set; }

        [StringLength(1000)]
        [Display(Name = "Description")]
        public string? Description { get; set; }

        [StringLength(600)]
        [Display(Name = "Logo URL")]
        public string? LogoUrl { get; set; }

        [StringLength(100)]
        [Display(Name = "Icon key")]
        public string? IconKey { get; set; }

        [StringLength(600)]
        [Display(Name = "Target URL")]
        public string? TargetUrl { get; set; }

        [StringLength(200)]
        [Display(Name = "Target entry key")]
        public string? TargetEntryKey { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;

        [Display(Name = "Default sort order")]
        public int DefaultSortOrder { get; set; } = 1000;
    }

    public sealed class EditInputModel
    {
        public int PortalEntryId { get; set; }

        [Required]
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [Display(Name = "Parent entry")]
        public int? ParentEntryId { get; set; }

        [StringLength(1000)]
        [Display(Name = "Description")]
        public string? Description { get; set; }

        [StringLength(600)]
        [Display(Name = "Logo URL")]
        public string? LogoUrl { get; set; }

        [StringLength(100)]
        [Display(Name = "Icon key")]
        public string? IconKey { get; set; }

        [StringLength(600)]
        [Display(Name = "Target URL")]
        public string? TargetUrl { get; set; }

        [StringLength(200)]
        [Display(Name = "Target entry key")]
        public string? TargetEntryKey { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;

        [Display(Name = "Default sort order")]
        public int DefaultSortOrder { get; set; } = 1000;
    }

    public sealed class LayoutEntryInput
    {
        public int PortalEntryId { get; set; }

        public int? ParentEntryId { get; set; }

        public bool IsEnabled { get; set; }

        public int SortOrder { get; set; }
    }
}
