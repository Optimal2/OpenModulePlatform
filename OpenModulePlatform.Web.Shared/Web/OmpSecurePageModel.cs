// File: OpenModulePlatform.Web.Shared/Web/OmpSecurePageModel.cs
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.Shared.Web;

/// <summary>
/// Base page model for pages that enforce OMP RBAC permissions.
/// </summary>
public abstract class OmpSecurePageModel<TResource> : OmpPageModel<TResource> where TResource : class
{
    private readonly RbacService _rbac;

    protected OmpSecurePageModel(IOptions<WebAppOptions> options, RbacService rbac)
        : base(options)
    {
        _rbac = rbac;
    }

    protected Task<HashSet<string>> GetUserPermissionsAsync(CancellationToken ct)
        => _rbac.GetUserPermissionsAsync(User, ct);

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        base.OnPageHandlerExecuting(context);
        ApplySecurePageCachingHeaders();
        ViewData["OmpPreventBackCache"] = true;
    }

    private void ApplySecurePageCachingHeaders()
    {
        var headers = HttpContext.Response.Headers;
        headers["Cache-Control"] = "no-store, no-cache, max-age=0, must-revalidate";
        headers["Pragma"] = "no-cache";
        headers["Expires"] = "0";
    }

    /// <summary>
    /// Enforces a set of permissions using the configured mode or an explicit override.
    /// </summary>
    protected async Task<IActionResult?> RequirePermissionsAsync(
        IEnumerable<string> requiredPermissions,
        PermissionMode? modeOverride,
        CancellationToken ct)
    {
        if (WebAppOptions.AllowAnonymous)
        {
            return null;
        }

        var required = requiredPermissions?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray() ?? Array.Empty<string>();

        if (required.Length == 0)
        {
            return null;
        }

        var mode = modeOverride ?? WebAppOptions.PermissionMode;
        var current = await _rbac.GetUserPermissionsAsync(User, ct);

        var allowed = mode switch
        {
            PermissionMode.All => required.All(current.Contains),
            _ => required.Any(current.Contains)
        };

        return allowed ? null : Forbid();
    }

    protected Task<IActionResult?> RequireAnyAsync(
        CancellationToken ct,
        params string[] requiredPermissions)
        => RequirePermissionsAsync(requiredPermissions, PermissionMode.Any, ct);

    protected Task<IActionResult?> RequireAllAsync(
        CancellationToken ct,
        params string[] requiredPermissions)
        => RequirePermissionsAsync(requiredPermissions, PermissionMode.All, ct);
}

public abstract class OmpSecurePageModel : OmpSecurePageModel<SharedResource>
{
    protected OmpSecurePageModel(IOptions<WebAppOptions> options, RbacService rbac)
        : base(options, rbac)
    {
    }
}
