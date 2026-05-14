// File: OpenModulePlatform.Portal/Pages/Admin/PortalEntries.cshtml.cs
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

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

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public IReadOnlyList<PortalEntryAdminRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles("Portal entries");
        Input.IsEnabled = true;
        return Page();
    }

    public async Task<IActionResult> OnPostCreate(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles("Portal entries");
        ValidateInput();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        await _portalEntries.CreateAsync(
            new PortalEntryCreateData
            {
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

        StatusMessage = T("Portal entry created.");
        return RedirectToPage("/Admin/PortalEntries");
    }

    private async Task LoadAsync(CancellationToken ct)
        => Rows = await _portalEntries.GetAdminRowsAsync(ct);

    private void ValidateInput()
    {
        Input.DisplayName = Input.DisplayName.Trim();

        if (string.IsNullOrWhiteSpace(Input.TargetUrl) && string.IsNullOrWhiteSpace(Input.TargetEntryKey))
        {
            ModelState.AddModelError(nameof(Input.TargetUrl), T("Enter a target URL or target entry key."));
        }

        if (!string.IsNullOrWhiteSpace(Input.TargetUrl) && !IsSafeTargetUrl(Input.TargetUrl))
        {
            ModelState.AddModelError(nameof(Input.TargetUrl), T("Use an absolute http/https URL, a local path starting with /, or a relative path."));
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
}
