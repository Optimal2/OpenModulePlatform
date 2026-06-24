using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Notifications;

[Authorize]
public sealed class TopBarNotificationHub : Hub
{
    public const string Path = "/topbar/notifications/updates";
    public const string StateChangedMethod = "notificationStateChanged";
    public const string BroadcastGroupName = "omp-broadcast";
    public const string AuthenticatedGroupName = "omp-authenticated";

    public override async Task OnConnectedAsync()
    {
        if (Context.User is not { } user
            || NotificationService.TryGetOmpUserId(user) is not int userId)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            UserGroupName(userId),
            Context.ConnectionAborted);

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            AuthenticatedGroupName,
            Context.ConnectionAborted);

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            BroadcastGroupName,
            Context.ConnectionAborted);

        await base.OnConnectedAsync();
    }

    internal static string UserGroupName(int userId)
        => string.Create(CultureInfo.InvariantCulture, $"omp-user:{userId}");
}
