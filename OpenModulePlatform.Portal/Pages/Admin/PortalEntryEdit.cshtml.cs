// File: OpenModulePlatform.Portal/Pages/Admin/PortalEntryEdit.cshtml.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class PortalEntryEditModel : OmpPortalPageModel
{
    private readonly PortalEntryService _portalEntries;

    public PortalEntryEditModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        PortalEntryService portalEntries)
        : base(options, rbac)
    {
        _portalEntries = portalEntries;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGet(int portalEntryId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Edit portal entry");
        var row = await _portalEntries.GetAdminRowAsync(portalEntryId, ct);
        if (row is null)
        {
            return NotFound();
        }

        Input = ToInput(row);
        return Page();
    }

    public async Task<IActionResult> OnPost(int portalEntryId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Edit portal entry");
        var entryId = Input.PortalEntryId > 0 ? Input.PortalEntryId : portalEntryId;
        var existing = await _portalEntries.GetAdminRowAsync(entryId, ct);
        if (existing is null)
        {
            return NotFound();
        }

        Input.PortalEntryId = existing.PortalEntryId;
        Input.EntryKey = existing.EntryKey;
        Input.SourceAppInstanceId = existing.SourceAppInstanceId;
        ValidateInput(existing);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var updated = await _portalEntries.UpdateAsync(
            new PortalEntryEditData
            {
                PortalEntryId = Input.PortalEntryId,
                DisplayName = Input.DisplayName.Trim(),
                Description = Clean(Input.Description),
                LogoUrl = Clean(Input.LogoUrl),
                IconKey = Clean(Input.IconKey),
                TargetUrl = Clean(Input.TargetUrl),
                TargetEntryKey = Clean(Input.TargetEntryKey),
                IsEnabled = Input.IsEnabled,
                DefaultSortOrder = Input.DefaultSortOrder
            },
            ct);

        if (!updated)
        {
            return NotFound();
        }

        StatusMessage = T("Portal entry updated.");
        return RedirectToPage("/Admin/PortalEntryEdit", new { portalEntryId = Input.PortalEntryId });
    }

    private void ValidateInput(PortalEntryAdminRow existing)
    {
        Input.DisplayName = Input.DisplayName?.Trim() ?? string.Empty;

        if (!existing.SourceAppInstanceId.HasValue
            && string.IsNullOrWhiteSpace(Input.TargetUrl)
            && string.IsNullOrWhiteSpace(Input.TargetEntryKey))
        {
            ModelState.AddModelError(nameof(Input.TargetUrl), T("Enter a target URL or target entry key."));
        }

        if (!string.IsNullOrWhiteSpace(Input.TargetUrl) && !IsSafeTargetUrl(Input.TargetUrl))
        {
            ModelState.AddModelError(nameof(Input.TargetUrl), T("Use an absolute http/https URL, a local path starting with /, or a relative path."));
        }
    }

    private static InputModel ToInput(PortalEntryAdminRow row)
        => new()
        {
            PortalEntryId = row.PortalEntryId,
            EntryKey = row.EntryKey,
            DisplayName = row.DisplayName,
            Description = row.Description,
            LogoUrl = row.LogoUrl,
            IconKey = row.IconKey,
            TargetUrl = row.TargetUrl,
            TargetEntryKey = row.TargetEntryKey,
            SourceAppInstanceId = row.SourceAppInstanceId,
            IsEnabled = row.IsEnabled,
            DefaultSortOrder = row.DefaultSortOrder
        };

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
        public int PortalEntryId { get; set; }

        public string EntryKey { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

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

        public Guid? SourceAppInstanceId { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;

        [Display(Name = "Default sort order")]
        public int DefaultSortOrder { get; set; } = 1000;
    }
}
