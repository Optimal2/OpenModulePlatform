using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace OpenModulePlatform.Web.ExampleWebAppBlazorModule.Components;

/// <summary>
/// Shared Blazor component base for the Example Web App Blazor Module.
/// </summary>
public abstract class ExampleWebAppBlazorModuleComponentBase : ComponentBase
{
    [CascadingParameter]
    private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    [Inject]
    protected IOptions<WebAppOptions> Options { get; set; } = default!;

    [Inject]
    protected RbacService Rbac { get; set; } = default!;

    protected ClaimsPrincipal CurrentUser { get; private set; } = new(new ClaimsIdentity());
    protected HashSet<string> CurrentPermissions { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    protected bool AuthorizationResolved { get; private set; }
    protected bool IsAuthorized { get; private set; }
    protected string? AccessDeniedMessage { get; private set; }
    protected string PageTitle { get; private set; } = "OpenModulePlatform";

    protected string WebAppTitle => string.IsNullOrWhiteSpace(Options.Value.Title)
        ? "OpenModulePlatform"
        : Options.Value.Title;

    protected string CurrentUserName => CurrentUser.Identity?.Name ?? "unknown";

    protected void SetPageTitle(string? pageTitle = null)
    {
        PageTitle = string.IsNullOrWhiteSpace(pageTitle)
            ? WebAppTitle
            : $"{WebAppTitle} - {pageTitle}";
    }

    protected async Task<bool> EnsureAnyPermissionAsync(
        CancellationToken ct,
        params string[] requiredPermissions)
    {
        var authState = AuthenticationStateTask is null
            ? new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))
            : await AuthenticationStateTask;

        CurrentUser = authState.User;

        if (Options.Value.AllowAnonymous)
        {
            CurrentPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IsAuthorized = true;
            AuthorizationResolved = true;
            AccessDeniedMessage = null;
            return true;
        }

        if (CurrentUser.Identity?.IsAuthenticated != true)
        {
            CurrentPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IsAuthorized = false;
            AuthorizationResolved = true;
            AccessDeniedMessage = "Du saknar åtkomst till sidan.";
            return false;
        }

        CurrentPermissions = await Rbac.GetUserPermissionsAsync(CurrentUser, ct);

        var required = requiredPermissions
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        IsAuthorized = required.Length == 0 || required.Any(CurrentPermissions.Contains);
        AuthorizationResolved = true;
        AccessDeniedMessage = IsAuthorized
            ? null
            : "Du saknar behörighet för den här sidan.";

        return IsAuthorized;
    }
}
