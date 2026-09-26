// File: OpenModulePlatform.Portal/Pages/Admin/OmpPortalPageModel.cs
using System.Globalization;
using OpenModulePlatform.ModuleDefinitions;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Portal.Security;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Base class for Portal admin pages.
/// It centralizes the permission check for OMP.Portal.Admin so page models stay focused on their own data flow.
/// </summary>
/// <remarks>
/// The base is closed over PortalResource, not the SharedResource default. Without the type
/// argument every T("...") in every Portal admin page model read SharedResource (154 sv-SE
/// entries) while the Swedish text sat in PortalResource (2104 entries) -- so 295 keys had a
/// finished translation that was never read, and the operator saw the English key text. That
/// is invisible to a resx review, because the translations are there and look complete; the
/// only symptom is an English admin UI. PortalLocalizer below was added as a workaround and is
/// used on three pages; it still works, and now agrees with T() (R8-P5-1).
/// </remarks>
public abstract class OmpPortalPageModel : OmpSecurePageModel<PortalResource>
{
    protected OmpPortalPageModel(IOptions<WebAppOptions> options, RbacService rbac)
        : base(options, rbac)
    {
    }

    /// <summary>
    /// Portal resource localizer. The admin pages' <c>T()</c> helper targets
    /// SharedResource, so page-specific texts (including localized exception
    /// display via <see cref="PortalTextLocalizer"/>) resolve through this
    /// localizer instead.
    /// </summary>
    protected IStringLocalizer<PortalResource> PortalLocalizer =>
        HttpContext.RequestServices.GetRequiredService<IStringLocalizer<PortalResource>>();

    protected async Task<IActionResult?> RequirePortalAdminAsync(CancellationToken ct)
    {
        var result = await RequireAnyAsync(ct, OmpPortalPermissions.Admin);
        ViewData["IsPortalAdmin"] = result is null;
        return result;
    }

    /// <summary>
    /// Guards the user log: a Portal administrator or a holder of the dedicated user log
    /// permission may read it. The layout gets the caller's permissions so its admin menu
    /// lists only the pages they can open (just the user log for a pure auditor).
    /// </summary>
    protected Task<IActionResult?> RequireUserLogReaderAsync(CancellationToken ct)
        => RequireLogReaderAsync(OmpPortalPermissions.UserLogView, ct);

    /// <summary>The system log's twin: a Portal administrator or a holder of the system log permission.</summary>
    protected Task<IActionResult?> RequireSystemLogReaderAsync(CancellationToken ct)
        => RequireLogReaderAsync(OmpPortalPermissions.SystemLogView, ct);

    private async Task<IActionResult?> RequireLogReaderAsync(string permission, CancellationToken ct)
    {
        var result = await RequireAnyAsync(ct, OmpPortalPermissions.Admin, permission);
        if (result is not null)
        {
            return result;
        }

        if (WebAppOptions.AllowAnonymous)
        {
            ViewData["IsPortalAdmin"] = true;
            return null;
        }

        var permissions = await GetUserPermissionsAsync(ct);
        ViewData["IsPortalAdmin"] = permissions.Contains(OmpPortalPermissions.Admin);
        ViewData["PortalPermissions"] = permissions;
        return null;
    }

    /// <summary>
    /// The operator message for a delete that module runtime maintenance stopped: the module key
    /// or schema and the event, never the SQL or error text, which goes to the log instead. Any
    /// other <see cref="InvalidOperationException"/> shows <paramref name="fallbackKey"/>.
    /// </summary>
    protected string DeleteBlockedMessage(InvalidOperationException ex, string fallbackKey)
    {
        HttpContext.RequestServices.GetService<ILoggerFactory>()?
            .CreateLogger(GetType())
            .LogWarning(ex, "Delete refused: {Reason}", ex.Message);

        if (ex is not ModuleRuntimeMaintenanceException blocked)
        {
            return T(fallbackKey);
        }

        return blocked.Failure switch
        {
            ModuleRuntimeMaintenanceFailure.StepFailed when blocked.ModuleKey is not null => string.Format(
                CultureInfo.CurrentCulture,
                T("The runtime maintenance step of module '{0}' failed for event '{1}'. Nothing was deleted; the Portal log has the details."),
                blocked.ModuleKey,
                blocked.EventName),
            ModuleRuntimeMaintenanceFailure.UnreleasedModuleRows when blocked.ModuleKey is not null => string.Format(
                CultureInfo.CurrentCulture,
                T("Module '{0}' still has rows that reference this item and declares no runtime maintenance step for event '{1}' that releases them. Upgrade the module, then retry. Nothing was deleted."),
                blocked.ModuleKey,
                blocked.EventName),
            ModuleRuntimeMaintenanceFailure.UnregisteredSchemaRows when blocked.SchemaName is not null => string.Format(
                CultureInfo.CurrentCulture,
                T("Schema '{0}' belongs to no registered module (a leftover schema?) and still has rows that reference this item. Clean up the schema, then retry. Nothing was deleted."),
                blocked.SchemaName),
            ModuleRuntimeMaintenanceFailure.EventRefused => string.Format(
                CultureInfo.CurrentCulture,
                T("Runtime maintenance for event '{0}' was refused because a module definition could not be validated. Nothing was deleted; the Portal log has the details."),
                blocked.EventName),
            _ => string.Format(
                CultureInfo.CurrentCulture,
                T("Another table still references this item. If a module owns that table, it needs a runtime maintenance step for event '{0}'. Nothing was deleted; the Portal log has the details."),
                blocked.EventName),
        };
    }
}
