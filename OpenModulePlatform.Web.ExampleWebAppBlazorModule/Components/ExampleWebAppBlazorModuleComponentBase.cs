using OpenModulePlatform.Web.ExampleWebAppBlazorModule.Localization;
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Models;
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
    protected NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    protected IStringLocalizer<ExampleWebAppBlazorModuleResource> L { get; set; } = default!;

    [Inject]
    protected IStringLocalizer<SharedResource> SharedL { get; set; } = default!;

    protected ClaimsPrincipal CurrentUser { get; private set; } = new(new ClaimsIdentity());
    protected HashSet<string> CurrentPermissions { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    protected bool AuthorizationResolved { get; private set; }
    protected bool IsAuthorized { get; private set; }
    protected string? AccessDeniedMessage { get; private set; }
    protected OmpErrorDisplayModel? PageError { get; private set; }
    protected string PageTitle { get; private set; } = "OpenModulePlatform";

    protected string WebAppTitle => string.IsNullOrWhiteSpace(Options.Value.Title)
        ? "OpenModulePlatform"
        : Options.Value.Title;

    protected string CurrentUserName => CurrentUser.Identity?.Name ?? L["Unknown user"];

    protected void SetPageTitle(string? pageTitle = null)
    {
        PageTitle = string.IsNullOrWhiteSpace(pageTitle)
            ? WebAppTitle
            : $"{WebAppTitle} - {pageTitle}";
    }

    protected string AppHref(string relativePath = "")
        => string.IsNullOrWhiteSpace(relativePath)
            ? string.Empty
            : relativePath.TrimStart('/');

    protected void ClearPageError()
        => PageError = null;

    protected void ShowForbiddenError(string? messageOverride = null)
    {
        PageError = OmpErrorDisplayModelFactory.CreateForbidden(
            GetCurrentPathAndQuery(),
            OmpUrlPathHelper.CombinePortalHref(Options.Value.PortalTopBar.PortalBaseUrl, "/"),
            NavigationManager.BaseUri,
            SharedL,
            messageOverride);
        SetPageTitle(SharedL["StatusPageTitle403"]);
    }

    protected void ShowNotFoundError(string? messageOverride = null)
    {
        PageError = OmpErrorDisplayModelFactory.CreateNotFound(
            GetCurrentPathAndQuery(),
            OmpUrlPathHelper.CombinePortalHref(Options.Value.PortalTopBar.PortalBaseUrl, "/"),
            NavigationManager.BaseUri,
            SharedL,
            messageOverride);
        SetPageTitle(SharedL["StatusPageTitle404"]);
    }

    private string GetCurrentPathAndQuery()
    {
        if (!Uri.TryCreate(NavigationManager.Uri, UriKind.Absolute, out var currentUri))
        {
            return NavigationManager.Uri;
        }

        return $"{currentUri.AbsolutePath}{currentUri.Query}";
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
            ClearPageError();
            return true;
        }

        if (CurrentUser.Identity?.IsAuthenticated != true)
        {
            CurrentPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IsAuthorized = false;
            AuthorizationResolved = true;
            AccessDeniedMessage = L["You do not have access to the page."].Value;
            ShowForbiddenError(AccessDeniedMessage);
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
            : L["You do not have permission for this page."].Value;

        if (IsAuthorized)
        {
            ClearPageError();
        }
        else
        {
            ShowForbiddenError(AccessDeniedMessage);
        }

        return IsAuthorized;
    }
}
