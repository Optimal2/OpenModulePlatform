// File: OpenModulePlatform.Portal/Pages/Admin/Rbac/AdPrincipalMigration.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.Globalization;

namespace OpenModulePlatform.Portal.Pages.Admin.Rbac;

/// <summary>
/// Bulk move of AD-based role principal rows (ADUser/User) to the linked OMP users
/// (campaign ad-principalformen-hela-vagen-adfs-till-rbac, DEL 3). The source AD rows
/// are retained by design; the view says so plainly. AD group rows are never touched.
/// </summary>
public sealed class AdPrincipalMigrationModel : Pages.Admin.OmpPortalPageModel
{
    private const string PageTitleKey = "Move AD role principals to OMP users";

    private readonly AdRolePrincipalMigrationRepository _repo;

    public AdPrincipalMigrationModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        AdRolePrincipalMigrationRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public bool ConfirmExecute { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public IReadOnlyList<AdRolePrincipalMigrationDecision> Rows { get; private set; } = [];

    public AdRolePrincipalMigrationReport? Report { get; private set; }

    public int MoveCount { get; private set; }

    public int AlreadyPresentCount { get; private set; }

    public int SkipCount { get; private set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadPreviewAsync(ct);
        SetTitles(PageTitleKey);
        return Page();
    }

    public async Task<IActionResult> OnPostExecute(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (!ConfirmExecute)
        {
            ModelState.AddModelError(
                nameof(ConfirmExecute),
                T("Tick the confirmation box to run the bulk move."));
            await LoadPreviewAsync(ct);
            SetTitles(PageTitleKey);
            return Page();
        }

        Report = await _repo.ExecuteAsync(ct);
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            T("Bulk move completed: {0} assignment(s) created, {1} already present, {2} skipped. Source AD rows were retained."),
            Report.Created.Count.ToString(CultureInfo.InvariantCulture),
            Report.AlreadyPresentCount.ToString(CultureInfo.InvariantCulture),
            Report.Skipped.Count.ToString(CultureInfo.InvariantCulture));

        await LoadPreviewAsync(ct);
        SetTitles(PageTitleKey);
        return Page();
    }

    public string OutcomeText(AdRolePrincipalMigrationDecision row)
        => row.Outcome switch
        {
            AdRolePrincipalMigrationOutcome.Move => string.Format(
                CultureInfo.CurrentCulture,
                T("Move to OMP user {0} (id: {1})"),
                row.TargetDisplayName,
                row.TargetUserId?.ToString(CultureInfo.InvariantCulture)),
            AdRolePrincipalMigrationOutcome.AlreadyPresent => string.Format(
                CultureInfo.CurrentCulture,
                T("Already assigned to OMP user {0} (id: {1}); nothing to insert."),
                row.TargetDisplayName,
                row.TargetUserId?.ToString(CultureInfo.InvariantCulture)),
            _ => SkipReasonText(row.SkipReason)
        };

    public string SkipReasonText(AdRolePrincipalMigrationSkipReason reason)
        => reason switch
        {
            AdRolePrincipalMigrationSkipReason.NoEnabledAdLink =>
                T("Skipped: no enabled AD link matches this principal."),
            AdRolePrincipalMigrationSkipReason.AmbiguousLinkedUsers =>
                T("Skipped: the principal resolves to more than one active OMP user."),
            AdRolePrincipalMigrationSkipReason.LinkedUserInactive =>
                T("Skipped: the linked OMP user is inactive."),
            AdRolePrincipalMigrationSkipReason.UnsupportedPrincipalType =>
                T("Skipped: this principal type is not moved."),
            _ => T("Skipped.")
        };

    private async Task LoadPreviewAsync(CancellationToken ct)
    {
        Rows = await _repo.PreviewAsync(ct);
        MoveCount = Rows.Count(static row => row.Outcome == AdRolePrincipalMigrationOutcome.Move);
        AlreadyPresentCount = Rows.Count(static row => row.Outcome == AdRolePrincipalMigrationOutcome.AlreadyPresent);
        SkipCount = Rows.Count(static row => row.Outcome == AdRolePrincipalMigrationOutcome.Skipped);
    }
}
