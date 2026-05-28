// File: OpenModulePlatform.Portal/Pages/Index.cshtml.cs
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Security;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SharedRbacService = OpenModulePlatform.Web.Shared.Services.RbacService;
using System.Security.Claims;
using System.Text.Json;

namespace OpenModulePlatform.Portal.Pages;

/// <summary>
/// Portal start page showing the user's dashboard.
/// </summary>
public sealed class IndexModel : OmpPageModel<PortalResource>
{
    private readonly PortalDashboardService _dashboard;
    private readonly PortalEntryService _portalEntries;
    private readonly PortalBlankWidgetService _blankWidget;
    private readonly PortalMusicPlayerService _musicPlayer;
    private readonly SharedRbacService _rbac;
    private readonly OmpAdminRepository _repo;

    public IndexModel(
        IOptions<WebAppOptions> options,
        PortalDashboardService dashboard,
        PortalEntryService portalEntries,
        PortalBlankWidgetService blankWidget,
        PortalMusicPlayerService musicPlayer,
        SharedRbacService rbac,
        OmpAdminRepository repo)
        : base(options)
    {
        _dashboard = dashboard;
        _portalEntries = portalEntries;
        _blankWidget = blankWidget;
        _musicPlayer = musicPlayer;
        _rbac = rbac;
        _repo = repo;
    }

    public IReadOnlyList<DashboardActiveWidget> ActiveWidgets { get; private set; } = [];

    public IReadOnlyList<DashboardWidgetDefinition> AvailableWidgets { get; private set; } = [];

    public bool CanEditDashboard { get; private set; }

    public bool IsPortalAdmin { get; private set; }

    public bool DashboardAlignToGrid { get; private set; } = true;

    public bool DashboardExpandedCanvas { get; private set; } = true;

    public string DashboardDraftKey { get; private set; } = string.Empty;

    public OverviewMetrics Metrics { get; private set; } = new();

    public IReadOnlyList<PortalEntry> FavoritePortalEntries { get; private set; } = [];

    public IReadOnlyList<PortalEntry> AllPortalEntries { get; private set; } = [];

    public IReadOnlyList<DashboardRoleOption> DashboardRoles { get; private set; } = [];

    public IReadOnlyList<DashboardNavbarSection> DashboardNavbarSections { get; private set; } = [];

    public IReadOnlyList<DashboardContentPageLink> ContentPages { get; private set; } = [];

    public async Task OnGet(bool manage = false, bool fullList = false, CancellationToken ct = default)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnGetMusicPlaylist(CancellationToken ct)
    {
        var playlist = await _musicPlayer.GetPlaylistAsync(
            id => Url.Page("/Index", "MusicTrack", new { id }) ?? string.Empty,
            ct);
        return new JsonResult(playlist);
    }

    public async Task<IActionResult> OnGetMusicTrack(long id, CancellationToken ct)
    {
        var track = await _musicPlayer.GetTrackFileAsync(id, ct);
        if (track is null)
        {
            return NotFound();
        }

        return new FileContentResult(track.Data, track.ContentType)
        {
            FileDownloadName = track.FileName,
            EnableRangeProcessing = true
        };
    }

    public async Task<IActionResult> OnGetBlankWidgetImages(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out _))
        {
            return Forbid();
        }

        var images = await _blankWidget.GetImagesAsync(
            id => Url.Page("/Index", "BlankWidgetImage", new { id }) ?? string.Empty,
            ct);
        return new JsonResult(images);
    }

    public async Task<IActionResult> OnGetBlankWidgetImage(long id, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out _))
        {
            return Forbid();
        }

        var image = await _blankWidget.GetImageFileAsync(id, ct);
        if (image is null)
        {
            return NotFound();
        }

        return new FileContentResult(image.Data, image.ContentType)
        {
            FileDownloadName = image.FileName,
            EnableRangeProcessing = false
        };
    }

    public async Task<IActionResult> OnPostAddWidget(int widgetId, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        var roleContext = await _rbac.GetUserRoleContextAsync(User, ct);
        var widget = await _dashboard.CreateWidgetDraftAsync(
            widgetId,
            roleContext.EffectiveRoleIds.ToHashSet(),
            roleContext.EffectivePermissions,
            ct);
        if (widget is null)
        {
            return Forbid();
        }

        return new JsonResult(ToDashboardWidgetDto(widget));
    }

    public async Task<IActionResult> OnPostSaveDashboard(string widgetsJson, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        var updates = string.IsNullOrWhiteSpace(widgetsJson)
            ? new List<DashboardWidgetLayoutUpdate>()
            : JsonSerializer.Deserialize<List<DashboardWidgetLayoutUpdate>>(
                widgetsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

        var roleContext = await _rbac.GetUserRoleContextAsync(User, ct);
        var result = await _dashboard.SaveLayoutAsync(
            userId,
            updates,
            roleContext.EffectiveRoleIds.ToHashSet(),
            roleContext.EffectivePermissions,
            ct);
        return new JsonResult(new { ok = true, addedWidgets = result.CreatedWidgets });
    }

    public async Task<IActionResult> OnPostSaveDashboardPreference(bool alignToGrid, bool expandedCanvas, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        await _dashboard.SetPreferencesAsync(userId, alignToGrid, expandedCanvas, ct);
        return new JsonResult(new { ok = true, alignToGrid, expandedCanvas });
    }

    public async Task<IActionResult> OnPostRemoveWidget(long userActiveWidgetId, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        await _dashboard.RemoveWidgetAsync(userId, userActiveWidgetId, ct);
        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnPostResetDashboard(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        await _dashboard.ResetDashboardAsync(userId, ct);
        return new JsonResult(new { ok = true });
    }

    public async Task<IActionResult> OnPostUploadMusicTrack(
        IFormFile? file,
        string? title,
        string? artist,
        string? attribution,
        string? source,
        string? description,
        CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId) || !await IsPortalAdminAsync(ct))
        {
            return Forbid();
        }

        if (file is null)
        {
            return BadRequest(new { ok = false, message = "Upload one MP3 file." });
        }

        try
        {
            var result = await _musicPlayer.AddTrackAsync(
                file,
                new MusicPlayerTrackInput(title, artist, attribution, source, description),
                userId,
                ct);
            return new JsonResult(new { ok = true, result.AddedTracks, result.ReusedTracks });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { ok = false, message = ex.Message });
        }
    }

    public async Task<IActionResult> OnPostUploadMusicPlaylistZip(IFormFile? zipFile, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId) || !await IsPortalAdminAsync(ct))
        {
            return Forbid();
        }

        if (zipFile is null)
        {
            return BadRequest(new { ok = false, message = "Upload a zip file." });
        }

        try
        {
            var result = await _musicPlayer.ImportZipAsync(zipFile, userId, ct);
            return new JsonResult(new { ok = true, result.AddedTracks, result.ReusedTracks });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { ok = false, message = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new { ok = false, message = ex.Message });
        }
    }

    public async Task<IActionResult> OnPostUploadBlankWidgetImage(
        IFormFile? file,
        string? displayName,
        CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId) || !await IsPortalAdminAsync(ct))
        {
            return Forbid();
        }

        if (file is null)
        {
            return BadRequest(new { ok = false, message = "Upload one image or GIF file." });
        }

        try
        {
            var result = await _blankWidget.AddImageAsync(file, displayName, userId, ct);
            var images = await _blankWidget.GetImagesAsync(
                id => Url.Page("/Index", "BlankWidgetImage", new { id }) ?? string.Empty,
                ct);
            return new JsonResult(new
            {
                ok = true,
                result.AddedImages,
                result.ReusedImages,
                selectedImageId = result.ImageIds.LastOrDefault(),
                images = images.Images
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { ok = false, message = ex.Message });
        }
    }

    public async Task<IActionResult> OnPostUploadBlankWidgetImagesZip(IFormFile? zipFile, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId) || !await IsPortalAdminAsync(ct))
        {
            return Forbid();
        }

        if (zipFile is null)
        {
            return BadRequest(new { ok = false, message = "Upload a zip file." });
        }

        try
        {
            var result = await _blankWidget.ImportZipAsync(zipFile, userId, ct);
            var images = await _blankWidget.GetImagesAsync(
                id => Url.Page("/Index", "BlankWidgetImage", new { id }) ?? string.Empty,
                ct);
            return new JsonResult(new
            {
                ok = true,
                result.AddedImages,
                result.ReusedImages,
                selectedImageId = result.ImageIds.LastOrDefault(),
                images = images.Images
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { ok = false, message = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new { ok = false, message = ex.Message });
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        SetTitles();

        var roleContext = await _rbac.GetUserRoleContextAsync(User, ct);
        var permissions = roleContext.EffectivePermissions;
        var roleIds = roleContext.EffectiveRoleIds.ToHashSet();
        IsPortalAdmin = permissions.Contains(OmpPortalPermissions.Admin);
        ViewData["IsPortalAdmin"] = IsPortalAdmin;
        DashboardNavbarSections = BuildDashboardNavbarSections(IsPortalAdmin);
        DashboardRoles = roleContext.AvailableRoles
            .Select(role => new DashboardRoleOption(
                role.RoleId,
                role.Name,
                role.Description,
                role.RoleId == roleContext.ActiveRoleId))
            .OrderByDescending(role => role.IsActive)
            .ThenBy(role => role.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var userId = TryGetCurrentUserId(out var resolvedUserId)
            ? resolvedUserId
            : (int?)null;
        CanEditDashboard = userId.HasValue;

        if (IsPortalAdmin)
        {
            Metrics = await _repo.GetOverviewAsync(ct);
        }

        if (userId.HasValue)
        {
            DashboardDraftKey = $"portal-dashboard-user-{userId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var preferences = await _dashboard.GetPreferencesAsync(userId.Value, ct);
            DashboardAlignToGrid = preferences.AlignToGrid;
            DashboardExpandedCanvas = preferences.ExpandedCanvas;
            ActiveWidgets = await _dashboard.GetActiveWidgetsAsync(userId.Value, roleIds, permissions, ct);
            AvailableWidgets = await _dashboard.GetAvailableWidgetsAsync(roleIds, permissions, ct);
            AllPortalEntries = await _portalEntries.GetEntriesAsync(Request, userId.Value, permissions, includeHidden: false, ct);
            FavoritePortalEntries = await _portalEntries.GetNavigationFavoriteEntriesAsync(Request, userId.Value, permissions, ct);
            ContentPages = await _dashboard.GetReadableContentPagesAsync(Request, roleIds, permissions, ct);
        }
        else
        {
            ActiveWidgets = [];
            AvailableWidgets = [];
            AllPortalEntries = [];
            FavoritePortalEntries = [];
            DashboardNavbarSections = BuildDashboardNavbarSections(IsPortalAdmin);
            ContentPages = [];
        }
    }

    private IReadOnlyList<DashboardNavbarSection> BuildDashboardNavbarSections(bool isPortalAdmin)
    {
        if (!isPortalAdmin)
        {
            return [];
        }

        return PortalAdminNavigation
            .CreateSections(relativePath => Url.Content($"~{relativePath}"))
            .Select(section => new DashboardNavbarSection(
                section.TextKey,
                section.Items
                    .Select(item => new DashboardNavbarLink(item.TextKey, item.Href))
                    .ToArray()))
            .ToArray();
    }

    private bool TryGetCurrentUserId(out int userId)
    {
        var userIdClaim = User.FindFirstValue(OpenModulePlatform.Web.Shared.Security.OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(userIdClaim, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out userId);
    }

    private async Task<bool> IsPortalAdminAsync(CancellationToken ct)
    {
        var roleContext = await _rbac.GetUserRoleContextAsync(User, ct);
        return roleContext.EffectivePermissions.Contains(OmpPortalPermissions.Admin);
    }

    private static object ToDashboardWidgetDto(DashboardActiveWidget widget)
        => new
        {
            userActiveWidgetId = widget.UserActiveWidgetId,
            widgetId = widget.WidgetId,
            title = widget.EffectiveTitle,
            widgetType = widget.WidgetType,
            payload = widget.Payload,
            offsetTop = widget.OffsetTop,
            offsetLeft = widget.OffsetLeft,
            width = widget.Width,
            height = widget.Height,
            orderPriority = widget.OrderPriority,
            intData = widget.IntData,
            stringData = widget.StringData,
            contentScale = widget.ContentScale,
            hideTitlebarWhenViewing = widget.HideTitlebarWhenViewing
        };
}
