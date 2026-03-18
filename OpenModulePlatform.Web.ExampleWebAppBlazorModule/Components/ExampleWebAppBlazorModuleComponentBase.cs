using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
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

    [Inject]
    protected IStringLocalizer<SharedResource> Localizer { get; set; } = default!;

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

    protected string T(string key) => Localizer[key];

    protected void SetPageTitle(string? pageTitle = null)
    {
        var localizedPageTitle = string.IsNullOrWhiteSpace(pageTitle)
            ? null
            : T(pageTitle);

        PageTitle = string.IsNullOrWhiteSpace(localizedPageTitle)
            ? WebAppTitle
            : $"{WebAppTitle} - {localizedPageTitle}";
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
            AccessDeniedMessage = T("You do not have access to this page.");
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
            : T("You do not have permission to view this page.");

        return IsAuthorized;
    }
}
