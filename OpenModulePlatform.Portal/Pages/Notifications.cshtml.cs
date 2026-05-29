// File: OpenModulePlatform.Portal/Pages/Notifications.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using System.Globalization;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Pages;

public sealed class NotificationsModel : OmpSecurePageModel<PortalResource>
{
    private readonly NotificationService _notifications;

    public NotificationsModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        NotificationService notifications)
        : base(options, rbac)
    {
        _notifications = notifications;
    }

    public IReadOnlyList<PortalTopBarNotification> Rows { get; private set; } = [];

    public bool CanUseNotifications { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        SetTitles("Notifications");
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostOpen(long notificationId, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        var result = await _notifications.MarkAsReadAsync(userId, notificationId, ct);
        if (!result.Success)
        {
            return Forbid();
        }

        if (IsSafeLocalDestination(result.DestinationUrl))
        {
            return LocalRedirect(result.DestinationUrl!);
        }

        return RedirectToPage("/Notifications");
    }

    public async Task<IActionResult> OnPostMarkAllRead(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        await _notifications.MarkAllAsReadAsync(userId, ct);
        StatusMessage = T("Notifications marked as read.");
        return RedirectToPage("/Notifications");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            CanUseNotifications = false;
            Rows = [];
            return;
        }

        CanUseNotifications = true;
        Rows = await _notifications.GetRecentForUserAsync(userId, 50, ct);
    }

    private bool TryGetCurrentUserId(out int userId)
    {
        var userIdClaim = User.FindFirstValue(OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(userIdClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId)
            && userId > 0;
    }

    private static bool IsSafeLocalDestination(string? destinationUrl)
        => !string.IsNullOrWhiteSpace(destinationUrl)
           && destinationUrl.StartsWith("/", StringComparison.Ordinal)
           && !destinationUrl.StartsWith("//", StringComparison.Ordinal)
           && !destinationUrl.Contains('\\', StringComparison.Ordinal);
}
