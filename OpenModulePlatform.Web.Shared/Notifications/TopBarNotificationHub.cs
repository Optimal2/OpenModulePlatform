using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Notifications;

[Authorize]
public sealed class TopBarNotificationHub : Hub
{
    public const string Path = "/topbar/notifications/updates";
    public const string PushEventPath = "/push/events";
    public const string StateChangedMethod = "notificationStateChanged";
    public const string PushEventMethod = "pushEvent";
    public const string BroadcastGroupName = "omp-broadcast";
    public const string AuthenticatedGroupName = "omp-authenticated";

    private readonly RbacService _rbac;
    private readonly IOptions<WebAppOptions> _options;

    public TopBarNotificationHub(
        RbacService rbac,
        IOptions<WebAppOptions> options)
    {
        _rbac = rbac;
        _options = options;
    }

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

        var roleContext = await _rbac.GetUserRoleContextAsync(user, Context.ConnectionAborted);
        foreach (var roleId in roleContext.EffectiveRoleIds.Where(roleId => roleId > 0).Distinct())
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                RoleGroupName(roleId),
                Context.ConnectionAborted);
        }

        var options = _options.Value;
        await AddConfiguredGroupAsync(ModuleGroupName(options.ModuleKey));
        await AddConfiguredGroupAsync(AppGroupName(options.AppKey));
        await AddConfiguredGroupAsync(AppGroupName(options.AppInstanceKey));

        await base.OnConnectedAsync();
    }

    internal static string UserGroupName(int userId)
        => string.Create(CultureInfo.InvariantCulture, $"omp-user:{userId}");

    internal static string RoleGroupName(int roleId)
        => string.Create(CultureInfo.InvariantCulture, $"omp-role:{roleId}");

    internal static string AppGroupName(string appKey)
        => CreateNamedGroup("omp-app", appKey);

    internal static string ModuleGroupName(string moduleKey)
        => CreateNamedGroup("omp-module", moduleKey);

    private Task AddConfiguredGroupAsync(string groupName)
        => string.IsNullOrWhiteSpace(groupName)
            ? Task.CompletedTask
            : Groups.AddToGroupAsync(
                Context.ConnectionId,
                groupName,
                Context.ConnectionAborted);

    private static string CreateNamedGroup(string prefix, string value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized)
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"{prefix}:{normalized}");
    }
}
