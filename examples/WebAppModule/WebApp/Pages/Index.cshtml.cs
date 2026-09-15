// File: OpenModulePlatform.Web.ExampleWebAppModule/Pages/Index.cshtml.cs
using OpenModulePlatform.Web.ExampleWebAppModule.Services;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.ExampleWebAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.ExampleWebAppModule.Pages;

public sealed class IndexModel : ExampleWebAppModulePageModel
{
    private readonly ExampleWebAppModuleAdminRepository _repo;
    private readonly NotificationService _notifications;
    private readonly BannerService _banners;
    private readonly ILogger<IndexModel> _logger;
    private readonly ActivityLogWriter _activityLog;

    public IndexModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        ExampleWebAppModuleAdminRepository repo,
        NotificationService notifications,
        BannerService banners,
        ILogger<IndexModel> logger,
        ActivityLogWriter activityLog)
        : base(options, rbac)
    {
        _repo = repo;
        _notifications = notifications;
        _banners = banners;
        _logger = logger;
        _activityLog = activityLog;
    }

    public OverviewRow Overview { get; private set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        _logger.LogInformation("Loading ExampleWebAppModule overview page.");

        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            _logger.LogWarning("Access denied for ExampleWebAppModule overview page.");
            return guard;
        }

        SetTitles("Overview");
        Overview = await _repo.GetOverviewAsync(ct);

        _logger.LogInformation("ExampleWebAppModule overview loaded successfully.");
        return Page();
    }

    public async Task<IActionResult> OnPostSendTestNotification(CancellationToken ct)
    {
        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var userId = NotificationService.TryGetOmpUserId(User);
        if (userId is null)
        {
            ErrorMessage = T("Notifications require an OMP user");
            return RedirectToPage();
        }

        var notificationId = await _notifications.CreateForUserAsync(
            userId.Value,
            new NotificationCreateRequest(
                Title: T("Test notification"),
                Content: T("This notification was sent from ExampleModule."),
                DestinationUrl: BuildCurrentRelativeUrl(),
                Level: "info",
                CallerKey: "ExampleModule",
                CallerDisplayName: "ExampleModule",
                CallerIcon: "/_content/OpenModulePlatform.Web.Shared/icons/notifications.svg"),
            ct);

        // The OMP user id is known here (checked above), so the entry always has a person.
        await _activityLog.WriteAsync(new ActivityEntry
        {
            Event = "test_notification.sent",
            Summary = $"Sent test notification {notificationId} to themselves",
            Subject = new ActivitySubject("test_notification", notificationId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        }, User, CancellationToken.None);
        StatusMessage = T("Notification sent");
        return RedirectToPage();
    }

    public Task<IActionResult> OnPostSendLevel1Banner(CancellationToken ct)
        => CreateBannerAsync(
            BannerService.LevelAnnouncement,
            T("Test announcement banner"),
            T("This level 1 banner was sent from ExampleModule."),
            [new BannerTargetRequest(BannerService.TargetGlobal, null)],
            ct);

    public Task<IActionResult> OnPostSendLevel2Banner(CancellationToken ct)
        => CreateBannerAsync(
            BannerService.LevelWarning,
            T("Test warning banner"),
            T("This level 2 banner was sent from ExampleModule."),
            [new BannerTargetRequest(BannerService.TargetGlobal, null)],
            ct);

    public Task<IActionResult> OnPostSendLevel3Banner(CancellationToken ct)
        => CreateBannerAsync(
            BannerService.LevelCritical,
            T("Test critical banner"),
            T("This level 3 banner was sent from ExampleModule."),
            [new BannerTargetRequest(BannerService.TargetGlobal, null)],
            ct);

    public async Task<IActionResult> OnPostSendAdminBanner(CancellationToken ct)
    {
        var guard = await RequireAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var roleIds = await _banners.GetRoleIdsWithPermissionAsync("OMP.Portal.Admin", ct);
        if (roleIds.Count == 0)
        {
            ErrorMessage = T("No admin role was found.");
            return RedirectToPage();
        }

        return await CreateBannerAsync(
            BannerService.LevelWarning,
            T("Test admin banner"),
            T("This banner was sent from ExampleModule to portal admins."),
            roleIds.Select(roleId => new BannerTargetRequest(BannerService.TargetRole, roleId)).ToArray(),
            ct);
    }

    private async Task<IActionResult> CreateBannerAsync(
        int level,
        string title,
        string content,
        IReadOnlyCollection<BannerTargetRequest> targets,
        CancellationToken ct)
    {
        var guard = await RequireAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var bannerId = await _banners.CreateAsync(
            new BannerCreateRequest(
                Title: title,
                Content: content,
                Status: BannerService.StatusActive,
                Level: level,
                StartsAtUtc: null,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(2)),
            targets,
            ct);

        // Which kind of banner went where; the banner text is a fixed demo
        // string, so nothing of it needs to be in the log. Only for a signed-in
        // OMP user: an anonymous send (development mode) is not a person the
        // log can name.
        if (OmpUserIdentity.TryGetOmpUserId(User) is not null)
        {
            await _activityLog.WriteAsync(new ActivityEntry
            {
                Event = "banner.sent",
                Summary = $"Sent level {level} test banner {bannerId} to {targets.Count} target(s)",
                Subject = new ActivitySubject("banner", bannerId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Data = new Dictionary<string, object?>
                {
                    ["level"] = level,
                    ["targets"] = targets.Select(target => target.TargetType).Distinct().ToArray()
                }
            }, User, CancellationToken.None);
        }
        StatusMessage = T("Banner sent");
        return RedirectToPage();
    }

    private string BuildCurrentRelativeUrl()
    {
        var url = string.Concat(Request.PathBase, Request.Path);
        return string.IsNullOrWhiteSpace(url) ? "/" : url;
    }
}
