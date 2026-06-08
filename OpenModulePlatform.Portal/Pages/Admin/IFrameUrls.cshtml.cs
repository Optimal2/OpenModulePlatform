using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class IFrameUrlsModel : OmpPortalPageModel
{
    private readonly IFrameAdminService _iframeAdmin;

    public IFrameUrlsModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        IFrameAdminService iframeAdmin)
        : base(options, rbac)
    {
        _iframeAdmin = iframeAdmin;
    }

    [BindProperty]
    public UrlInputModel UrlInput { get; set; } = new();

    [BindProperty]
    public UrlSetInputModel UrlSetInput { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public IReadOnlyList<IFrameUrlAdminRow> UrlRows { get; private set; } = [];

    public IReadOnlyList<IFrameUrlSetAdminRow> UrlSetRows { get; private set; } = [];

    public bool HasIFrameTables { get; private set; }

    public string ActiveTab { get; private set; } = "urls";

    public async Task<IActionResult> OnGet(string? tab, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("iFrame URLs");
        ActiveTab = NormalizeTab(tab);
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateUrl(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("iFrame URLs");
        ActiveTab = "urls";
        await LoadAsync(ct);
        ModelState.Clear();
        NormalizeUrlInput();
        TryValidateModel(UrlInput, nameof(UrlInput));
        ValidateUrlInput();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await _iframeAdmin.CreateUrlAsync(
                new IFrameUrlCreateRequest(
                    UrlInput.DisplayName!,
                    UrlInput.Url!,
                    UrlInput.AllowedRoles,
                    UrlInput.Enabled),
                ct);

            StatusMessage = T("iFrame URL created.");
            return RedirectToPage("/Admin/IFrameUrls", new { tab = "urls" });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSetUrlEnabled(int urlId, bool enabled, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await _iframeAdmin.SetUrlEnabledAsync(urlId, enabled, ct);
        StatusMessage = enabled ? T("iFrame URL enabled.") : T("iFrame URL disabled.");
        return RedirectToPage("/Admin/IFrameUrls", new { tab = "urls" });
    }

    public async Task<IActionResult> OnPostCreateUrlSet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("iFrame URLs");
        ActiveTab = "sets";
        await LoadAsync(ct);
        ModelState.Clear();
        NormalizeUrlSetInput();
        TryValidateModel(UrlSetInput, nameof(UrlSetInput));
        ValidateUrlSetInput();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await _iframeAdmin.CreateUrlSetAsync(
                new IFrameUrlSetCreateRequest(
                    UrlSetInput.SetKey!,
                    UrlSetInput.DisplayName!,
                    UrlSetInput.SelectedUrlIds,
                    UrlSetInput.Enabled),
                ct);

            StatusMessage = T("iFrame URL set created.");
            return RedirectToPage("/Admin/IFrameUrls", new { tab = "sets" });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSetUrlSetEnabled(int urlSetId, bool enabled, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await _iframeAdmin.SetUrlSetEnabledAsync(urlSetId, enabled, ct);
        StatusMessage = enabled ? T("iFrame URL set enabled.") : T("iFrame URL set disabled.");
        return RedirectToPage("/Admin/IFrameUrls", new { tab = "sets" });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var data = await _iframeAdmin.GetAdminDataAsync(ct);
        HasIFrameTables = data.HasIFrameTables;
        UrlRows = data.Urls;
        UrlSetRows = data.UrlSets;
    }

    private void NormalizeUrlInput()
    {
        UrlInput.DisplayName = UrlInput.DisplayName?.Trim();
        UrlInput.Url = UrlInput.Url?.Trim();
        UrlInput.AllowedRoles = UrlInput.AllowedRoles?.Trim();
    }

    private void NormalizeUrlSetInput()
    {
        UrlSetInput.SetKey = UrlSetInput.SetKey?.Trim();
        UrlSetInput.DisplayName = UrlSetInput.DisplayName?.Trim();
        UrlSetInput.SelectedUrlIds = UrlSetInput.SelectedUrlIds
            .Where(urlId => urlId > 0)
            .Distinct()
            .ToList();
    }

    private void ValidateUrlInput()
    {
        if (!HasIFrameTables)
        {
            ModelState.AddModelError(string.Empty, T("The iFrame URL tables are not installed."));
        }
    }

    private void ValidateUrlSetInput()
    {
        if (!HasIFrameTables)
        {
            ModelState.AddModelError(string.Empty, T("The iFrame URL tables are not installed."));
        }

        if (UrlSetInput.SelectedUrlIds.Count == 0)
        {
            ModelState.AddModelError(
                $"{nameof(UrlSetInput)}.{nameof(UrlSetInputModel.SelectedUrlIds)}",
                T("Select at least one iFrame URL."));
        }
    }

    private static string NormalizeTab(string? tab)
        => string.Equals(tab, "sets", StringComparison.OrdinalIgnoreCase) ? "sets" : "urls";

    public sealed class UrlInputModel
    {
        [Required]
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string? DisplayName { get; set; }

        [Required]
        [StringLength(500)]
        [Display(Name = "URL")]
        public string? Url { get; set; }

        [StringLength(500)]
        [Display(Name = "Allowed roles")]
        public string? AllowedRoles { get; set; }

        [Display(Name = "Enabled")]
        public bool Enabled { get; set; } = true;
    }

    public sealed class UrlSetInputModel
    {
        [Required]
        [RegularExpression(@"^[A-Za-z0-9_.:-]+$")]
        [StringLength(100)]
        [Display(Name = "Set key")]
        public string? SetKey { get; set; }

        [Required]
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string? DisplayName { get; set; }

        [Display(Name = "URLs")]
        public List<int> SelectedUrlIds { get; set; } = [];

        [Display(Name = "Enabled")]
        public bool Enabled { get; set; } = true;
    }
}
