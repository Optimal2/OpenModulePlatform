// File: OpenModulePlatform.Portal/Pages/Admin/Banners.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class BannersModel : OmpPortalPageModel
{
    private const string TargetModeGlobal = "global";
    private const string TargetModeRoles = "roles";

    private readonly BannerService _banners;

    public BannersModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        BannerService banners)
        : base(options, rbac)
    {
        _banners = banners;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public IReadOnlyList<BannerAdminRow> Rows { get; private set; } = [];

    public IReadOnlyList<BannerRoleOption> RoleOptions { get; private set; } = [];

    public bool IsEdit => Input.BannerId > 0;

    public async Task<IActionResult> OnGet(long? bannerId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Banners");
        await LoadAsync(ct);

        if (bannerId is > 0)
        {
            var row = await _banners.GetForEditAsync(bannerId.Value, ct);
            if (row is null)
            {
                return NotFound();
            }

            Input = ToInput(row);
        }
        else
        {
            Input = new InputModel
            {
                Status = BannerService.StatusActive,
                Level = BannerService.LevelAnnouncement,
                TargetMode = TargetModeGlobal
            };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSave(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Banners");
        await LoadAsync(ct);
        ValidateInput();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            if (Input.BannerId > 0)
            {
                var updated = await _banners.UpdateAsync(
                    new BannerEditRequest(
                        Input.BannerId,
                        Input.Title,
                        Input.Content,
                        Input.Status,
                        Input.Level,
                        ToUtcOffset(Input.StartsAtUtc),
                        ToUtcOffset(Input.ExpiresAtUtc),
                        ToTargets()),
                    ct);

                if (!updated)
                {
                    return NotFound();
                }

                StatusMessage = T("Banner updated.");
                return RedirectToPage("/Admin/Banners", new { bannerId = Input.BannerId });
            }

            var bannerId = await _banners.CreateAsync(
                new BannerCreateRequest(
                    Input.Title,
                    Input.Content,
                    Input.Status,
                    Input.Level,
                    ToUtcOffset(Input.StartsAtUtc),
                    ToUtcOffset(Input.ExpiresAtUtc)),
                ToTargets(),
                ct);

            StatusMessage = T("Banner created.");
            return RedirectToPage("/Admin/Banners", new { bannerId });
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSetEnabled(long bannerId, bool enabled, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var updated = await _banners.SetEnabledAsync(bannerId, enabled, ct);
        StatusMessage = updated
            ? T(enabled ? "Banner enabled." : "Banner disabled.")
            : T("Banner was not found.");
        return RedirectToPage("/Admin/Banners");
    }

    public string StateText(string state)
        => state switch
        {
            "active" => T("Active"),
            "scheduled" => T("Scheduled"),
            "expired" => T("Expired"),
            "disabled" => T("Disabled"),
            _ => state
        };

    public string LevelText(int level)
        => level switch
        {
            BannerService.LevelCritical => T("Critical"),
            BannerService.LevelWarning => T("Warning"),
            _ => T("Announcement")
        };

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await _banners.GetAdminRowsAsync(ct);
        RoleOptions = await _banners.GetRoleOptionsAsync(ct);
    }

    private void ValidateInput()
    {
        Input.Title = Input.Title?.Trim();
        Input.Content = Input.Content?.Trim();
        Input.Status = string.IsNullOrWhiteSpace(Input.Status)
            ? BannerService.StatusActive
            : Input.Status.Trim().ToLowerInvariant();
        Input.TargetMode = string.IsNullOrWhiteSpace(Input.TargetMode)
            ? TargetModeGlobal
            : Input.TargetMode.Trim().ToLowerInvariant();
        Input.SelectedRoleIds = Input.SelectedRoleIds
            .Where(roleId => roleId > 0)
            .Distinct()
            .Order()
            .ToList();

        if (Input.Level is < BannerService.LevelAnnouncement or > BannerService.LevelCritical)
        {
            ModelState.AddModelError(nameof(Input.Level), T("Select a valid banner level."));
        }

        if (Input.Status is not (BannerService.StatusActive or BannerService.StatusDisabled))
        {
            ModelState.AddModelError(nameof(Input.Status), T("Select a valid status."));
        }

        if (Input.TargetMode == TargetModeRoles && Input.SelectedRoleIds.Count == 0)
        {
            ModelState.AddModelError(nameof(Input.SelectedRoleIds), T("Select at least one role."));
        }

        if (Input.TargetMode is not (TargetModeGlobal or TargetModeRoles))
        {
            ModelState.AddModelError(nameof(Input.TargetMode), T("Select a valid target."));
        }

        if (Input.StartsAtUtc.HasValue && Input.ExpiresAtUtc.HasValue && Input.ExpiresAtUtc.Value <= Input.StartsAtUtc.Value)
        {
            ModelState.AddModelError(nameof(Input.ExpiresAtUtc), T("Expires at must be after starts at."));
        }
    }

    private IReadOnlyList<BannerTargetRequest> ToTargets()
    {
        if (Input.TargetMode == TargetModeGlobal)
        {
            return [new BannerTargetRequest(BannerService.TargetGlobal, null)];
        }

        return Input.SelectedRoleIds
            .Select(roleId => new BannerTargetRequest(BannerService.TargetRole, roleId))
            .ToArray();
    }

    private static DateTimeOffset? ToUtcOffset(DateTime? value)
        => value.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc))
            : null;

    private static InputModel ToInput(BannerEditData row)
    {
        var roleIds = row.Targets
            .Where(target => string.Equals(target.TargetType, BannerService.TargetRole, StringComparison.OrdinalIgnoreCase))
            .Select(target => target.RoleId)
            .Where(roleId => roleId.HasValue)
            .Select(roleId => roleId!.Value)
            .ToList();

        return new InputModel
        {
            BannerId = row.BannerId,
            Title = row.Title,
            Content = row.Content,
            Status = row.Status,
            Level = row.Level,
            StartsAtUtc = row.StartsAtUtc,
            ExpiresAtUtc = row.ExpiresAtUtc,
            TargetMode = roleIds.Count > 0 ? TargetModeRoles : TargetModeGlobal,
            SelectedRoleIds = roleIds
        };
    }

    public sealed class InputModel
    {
        public long BannerId { get; set; }

        [Required]
        [StringLength(200)]
        [Display(Name = "Title")]
        public string? Title { get; set; }

        [Required]
        [StringLength(1000)]
        [Display(Name = "Content")]
        public string? Content { get; set; }

        [Display(Name = "Status")]
        public string Status { get; set; } = BannerService.StatusActive;

        [Display(Name = "Level")]
        public int Level { get; set; } = BannerService.LevelAnnouncement;

        [Display(Name = "Starts at (UTC)")]
        public DateTime? StartsAtUtc { get; set; }

        [Display(Name = "Expires at (UTC)")]
        public DateTime? ExpiresAtUtc { get; set; }

        [Display(Name = "Banner targets")]
        public string TargetMode { get; set; } = TargetModeGlobal;

        [Display(Name = "Specific roles")]
        public List<int> SelectedRoleIds { get; set; } = [];
    }
}
