// File: OpenModulePlatform.Web.Shared/Web/OmpSecurePageModel.cs
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.Shared.Web;

public abstract class OmpSecurePageModel : OmpPageModel
{
    private readonly RbacService _rbac;

    protected OmpSecurePageModel(IOptions<WebAppOptions> options, RbacService rbac)
        : base(options)
    {
        _rbac = rbac;
    }

    protected Task<HashSet<string>> GetUserPermissionsAsync(CancellationToken ct)
        => _rbac.GetUserPermissionsAsync(User, ct);

    protected async Task<IActionResult?> RequirePermissionsAsync(
        IEnumerable<string> requiredPermissions,
        PermissionMode? modeOverride,
        CancellationToken ct)
    {
        if (WebAppOptions.AllowAnonymous)
            return null;

        var required = requiredPermissions?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? Array.Empty<string>();
        if (required.Length == 0)
            return null;

        var mode = modeOverride ?? WebAppOptions.PermissionMode;
        var current = await _rbac.GetUserPermissionsAsync(User, ct);

        var allowed = mode switch
        {
            PermissionMode.All => required.All(current.Contains),
            _ => required.Any(current.Contains)
        };

        return allowed ? null : Forbid();
    }

    protected Task<IActionResult?> RequireAnyAsync(CancellationToken ct, params string[] requiredPermissions)
        => RequirePermissionsAsync(requiredPermissions, PermissionMode.Any, ct);

    protected Task<IActionResult?> RequireAllAsync(CancellationToken ct, params string[] requiredPermissions)
        => RequirePermissionsAsync(requiredPermissions, PermissionMode.All, ct);
}
