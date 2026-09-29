// File: OpenModulePlatform.Portal/Pages/Admin/Banners.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class BannersModel : OmpPortalPageModel
{
    private const string TargetModeGlobal = "global";
    private const string TargetModeRoles = "roles";
    public const string OccurrenceFirst = "first";
    public const string OccurrenceSecond = "second";

    private readonly OmpTime _time;
    private readonly BannerService _banners;
    private readonly IStringLocalizer<PortalResource> _portalLocalizer;

    public BannersModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        BannerService banners,
        OmpTime time,
        IStringLocalizer<PortalResource> portalLocalizer)
        : base(options, rbac)
    {
        _banners = banners;
        _time = time;
        _portalLocalizer = portalLocalizer;
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
                var stored = await _banners.GetForEditAsync(Input.BannerId, ct);
                if (stored is null)
                {
                    return NotFound();
                }

                var startsAt = ToUtcOffset(Input.StartsAt, Input.StartsAtOccurrence, stored.StartsAtUtc);
                var expiresAt = ToUtcOffset(Input.ExpiresAt, Input.ExpiresAtOccurrence, stored.ExpiresAtUtc);
                if (!ValidateScheduledOrder(startsAt, expiresAt))
                {
                    return Page();
                }

                var updated = await _banners.UpdateAsync(
                    new BannerEditRequest(
                        Input.BannerId,
                        Input.Title,
                        Input.Content,
                        Input.Status,
                        Input.Level,
                        startsAt,
                        expiresAt,
                        ToTargets()),
                    ct);

                if (!updated)
                {
                    return NotFound();
                }

                StatusMessage = P("Banner updated.");
                return RedirectToPage("/Admin/Banners", new { bannerId = Input.BannerId });
            }

            var newStartsAt = ToUtcOffset(Input.StartsAt, Input.StartsAtOccurrence);
            var newExpiresAt = ToUtcOffset(Input.ExpiresAt, Input.ExpiresAtOccurrence);
            if (!ValidateScheduledOrder(newStartsAt, newExpiresAt))
            {
                return Page();
            }

            var bannerId = await _banners.CreateAsync(
                new BannerCreateRequest(
                    Input.Title,
                    Input.Content,
                    Input.Status,
                    Input.Level,
                    newStartsAt,
                    newExpiresAt),
                ToTargets(),
                ct);

            StatusMessage = P("Banner created.");
            return RedirectToPage("/Admin/Banners", new { bannerId });
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, PortalTextLocalizer.Display(_portalLocalizer, ex.Message));
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
            ? P(enabled ? "Banner enabled." : "Banner disabled.")
            : P("Banner was not found.");
        return RedirectToPage("/Admin/Banners");
    }

    public async Task<IActionResult> OnPostSetEnabledMany(long[]? selectedBannerIds, bool enabled, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var bannerIds = (selectedBannerIds ?? [])
            .Where(bannerId => bannerId > 0)
            .Distinct()
            .ToArray();
        if (bannerIds.Length == 0)
        {
            StatusMessage = P("Select at least one banner.");
            return RedirectToPage("/Admin/Banners");
        }

        var updatedCount = 0;
        foreach (var bannerId in bannerIds)
        {
            updatedCount += await _banners.SetEnabledAsync(bannerId, enabled, ct) ? 1 : 0;
        }

        StatusMessage = updatedCount == 1
            ? P(enabled ? "Banner enabled." : "Banner disabled.")
            : string.Format(
                CultureInfo.CurrentCulture,
                P(enabled ? "{0} banners enabled." : "{0} banners disabled."),
                updatedCount);
        return RedirectToPage("/Admin/Banners");
    }

    public string StateText(string state)
        => state switch
        {
            "active" => P("Active"),
            "scheduled" => P("Scheduled"),
            "expired" => P("Expired"),
            "disabled" => P("Disabled"),
            _ => state
        };

    public string LevelText(int level)
        => level switch
        {
            BannerService.LevelCritical => P("Critical"),
            BannerService.LevelWarning => P("Warning"),
            _ => P("Announcement")
        };

    private string P(string key) => _portalLocalizer[key];

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
            ModelState.AddModelError(nameof(Input.Level), P("Select a valid banner level."));
        }

        if (Input.Status is not (BannerService.StatusActive or BannerService.StatusDisabled))
        {
            ModelState.AddModelError(nameof(Input.Status), P("Select a valid status."));
        }

        if (Input.TargetMode == TargetModeRoles && Input.SelectedRoleIds.Count == 0)
        {
            ModelState.AddModelError(nameof(Input.SelectedRoleIds), P("Select at least one role."));
        }

        if (Input.TargetMode is not (TargetModeGlobal or TargetModeRoles))
        {
            ModelState.AddModelError(nameof(Input.TargetMode), P("Select a valid target."));
        }

        ValidateCalendarInput(Input.StartsAt, "Input.StartsAt");
        ValidateCalendarInput(Input.ExpiresAt, "Input.ExpiresAt");
        Input.StartsAtOccurrence = NormalizeOccurrence(Input.StartsAtOccurrence);
        Input.ExpiresAtOccurrence = NormalizeOccurrence(Input.ExpiresAtOccurrence);
    }

    // Compared as instants: wall times misorder the two occurrences of a repeated minute.
    private bool ValidateScheduledOrder(DateTimeOffset? startsAt, DateTimeOffset? expiresAt)
    {
        if (startsAt.HasValue && expiresAt.HasValue && expiresAt.Value <= startsAt.Value)
        {
            ModelState.AddModelError("Input.ExpiresAt", P("Expires at must be after starts at."));
            return false;
        }

        return true;
    }

    private static string? NormalizeOccurrence(string? occurrence)
        => occurrence?.Trim().ToLowerInvariant() switch
        {
            OccurrenceFirst => OccurrenceFirst,
            OccurrenceSecond => OccurrenceSecond,
            _ => null
        };

    /// <summary>
    /// True when the wall time occurs twice in the presentation zone (the repeated
    /// autumn hour). The form then offers an explicit choice of occurrence, because
    /// the datetime field alone cannot tell the two instants apart.
    /// </summary>
    public bool IsRepeatedLocalTime(DateTime? value)
    {
        if (!value.HasValue)
        {
            return false;
        }

        try
        {
            var wallTime = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
            return _time.ToUtc(wallTime, upperBound: false) != _time.ToUtc(wallTime, upperBound: true);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The wall time with its zone abbreviation or offset for one occurrence.</summary>
    public string OccurrenceText(DateTime value, string occurrence)
    {
        var wallTime = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        var utc = _time.ToUtc(wallTime, upperBound: occurrence == OccurrenceSecond);
        return _time.Format(utc, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private void ValidateCalendarInput(DateTime? value, string field)
    {
        try
        {
            _ = ToUtcOffset(value, occurrence: null);
        }
        catch (ArgumentException)
        {
            ModelState.AddModelError(field, _portalLocalizer["Select a valid local time in {0}. The selected time does not exist or is not a calendar value.", _time.TimeZoneId]);
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

    // A repeated autumn minute uses the explicitly chosen occurrence. Without a
    // choice, an unchanged field keeps its stored instant (the wall-time field
    // cannot tell the two occurrences apart) and a new value uses the first.
    private DateTimeOffset? ToUtcOffset(DateTime? value, string? occurrence, DateTime? storedUtc = null)
    {
        if (!value.HasValue)
        {
            return null;
        }

        if (occurrence is OccurrenceFirst or OccurrenceSecond && IsRepeatedLocalTime(value))
        {
            return new DateTimeOffset(_time.ToUtc(value.Value, upperBound: occurrence == OccurrenceSecond));
        }

        if (storedUtc is { } stored && _time.ToDisplayTime(stored).DateTime == value.Value)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc));
        }

        return new DateTimeOffset(_time.ToUtc(value.Value, upperBound: false));
    }

    // The occurrence a stored instant has, so the choice starts at the stored value.
    private string? StoredOccurrence(DateTime? storedUtc)
    {
        if (storedUtc is not { } stored)
        {
            return null;
        }

        var wallTime = _time.ToDisplayTime(stored).DateTime;
        if (!IsRepeatedLocalTime(wallTime))
        {
            return null;
        }

        return _time.ToUtc(wallTime, upperBound: true) == DateTime.SpecifyKind(stored, DateTimeKind.Utc)
            ? OccurrenceSecond
            : OccurrenceFirst;
    }

    private InputModel ToInput(BannerEditData row)
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
            StartsAt = row.StartsAtUtc is { } starts ? _time.ToDisplayTime(starts).DateTime : null,
            ExpiresAt = row.ExpiresAtUtc is { } expires ? _time.ToDisplayTime(expires).DateTime : null,
            StartsAtOccurrence = StoredOccurrence(row.StartsAtUtc),
            ExpiresAtOccurrence = StoredOccurrence(row.ExpiresAtUtc),
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

        [Display(Name = "Starts at")]
        public DateTime? StartsAt { get; set; }

        [Display(Name = "Expires at")]
        public DateTime? ExpiresAt { get; set; }

        public string? StartsAtOccurrence { get; set; }

        public string? ExpiresAtOccurrence { get; set; }

        [Display(Name = "Banner targets")]
        public string TargetMode { get; set; } = TargetModeGlobal;

        [Display(Name = "Specific roles")]
        public List<int> SelectedRoleIds { get; set; } = [];
    }
}
