// File: OpenModulePlatform.Portal/Pages/Admin/InstanceTemplateEdit.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Shows the desired topology stored in the current installation profile.
/// This is the admin-facing source of truth that HostAgent materializes into runtime rows.
/// </summary>
public sealed class InstanceTemplateEditModel : OmpPortalPageModel
{
    private readonly OmpAdminRepository _repo;

    public InstanceTemplateEditModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public InstanceTemplateRow? Template { get; private set; }

    public IReadOnlyList<InstanceTemplateHostTopologyRow> Hosts { get; private set; } = [];

    public IReadOnlyList<InstanceTemplateModuleTopologyRow> Modules { get; private set; } = [];

    public IReadOnlyList<InstanceTemplateAppTopologyRow> Apps { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Carry-forward messages rendered inline on the page render instead of via
    /// flaky TempData that can be lost before the operator sees them (R5-F12).
    /// </summary>
    public IReadOnlyList<string> CarryForwardMessages { get; private set; } = [];

    public async Task<IActionResult> OnGet(int id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (!await LoadTemplateAsync(id, ct))
        {
            return NotFound();
        }

        SetTitles("Installation");
        return Page();
    }

    private async Task<bool> LoadTemplateAsync(int id, CancellationToken ct)
    {
        Template = await _repo.GetInstanceTemplateAsync(id, ct);
        if (Template is null)
        {
            return false;
        }

        Hosts = await _repo.GetInstanceTemplateHostsAsync(id, ct);
        Modules = await _repo.GetInstanceTemplateModulesAsync(id, ct);
        Apps = await _repo.GetInstanceTemplateAppsAsync(id, ct);
        return true;
    }

    public async Task<IActionResult> OnPostDeleteApp(int id, int templateId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            await _repo.DeleteInstanceTemplateAppInstanceAsync(id, ct);
            StatusMessage = T("Desired app removed.");
        }
        // R8-P5-7: see AppInstances -- one message for every SQL failure, and the
        // application THROW's own text discarded.
        catch (SqlException ex)
        {
            StatusMessage = T(PortalTextLocalizer.DescribeSqlError(
                ex,
                "The desired app could not be removed.",
                duplicateMessage: null,
                foreignKeyMessage: "The desired app could not be removed. Delete or update dependent rows first."));
        }

        return RedirectToPage("/Admin/InstanceTemplateEdit", new { id = templateId });
    }

    public async Task<IActionResult> OnPostUpgradeAppArtifact(
        int id,
        int templateId,
        int artifactId,
        CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            var (version, carryForward) = await _repo.UpgradeInstanceTemplateAppArtifactAsync(id, artifactId, ct);
            var successMessage = string.Format(T("Desired artifact updated to version {0}."), version);

            // Carry-forward outcomes are surfaced inline on the rendered page
            // rather than via TempData that can go stale (R5-F12).
            CarryForwardMessages = BuildCarryForwardMessages(carryForward);
            if (CarryForwardMessages.Count > 0)
            {
                StatusMessage = successMessage;
                if (!await LoadTemplateAsync(templateId, ct))
                {
                    return NotFound();
                }

                SetTitles("Installation");
                return Page();
            }

            StatusMessage = successMessage;
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = PortalTextLocalizer.Display(PortalLocalizer, ex.Message);
        }
        catch (SqlException ex)
        {
            StatusMessage = PortalLocalizer["The desired artifact could not be updated: {0}", PortalTextLocalizer.Display(PortalLocalizer, ex.Message)];
        }

        return RedirectToPage("/Admin/InstanceTemplateEdit", new { id = templateId });
    }

    private IReadOnlyList<string> BuildCarryForwardMessages(ArtifactConfigurationCarryForwardResult carryForward)
    {
        if (carryForward is null || string.IsNullOrWhiteSpace(carryForward.SourceVersion))
        {
            return [];
        }

        var messages = new List<string>();

        var preserved = carryForward.PathsWithOutcome(ArtifactConfigurationCarryForwardOutcome.Preserved);
        if (preserved.Count > 0)
        {
            messages.Add(string.Format(
                T("Preserved {0} operator-edited configuration file(s) from version {1}: {2}."),
                preserved.Count,
                carryForward.SourceVersion,
                string.Join(", ", preserved)));
        }

        var conflicts = carryForward.PathsWithOutcome(ArtifactConfigurationCarryForwardOutcome.Conflict);
        if (conflicts.Count > 0)
        {
            messages.Add(string.Format(
                T("Warning: configuration file(s) differ from version {0} and were taken from the package. Review them for lost operator edits: {1}."),
                carryForward.SourceVersion,
                string.Join(", ", conflicts)));
        }

        var missing = carryForward.PathsWithOutcome(ArtifactConfigurationCarryForwardOutcome.MissingInPackage);
        if (missing.Count > 0)
        {
            messages.Add(string.Format(
                T("Warning: operator-edited configuration file(s) from version {0} are not part of this package and were not carried forward: {1}."),
                carryForward.SourceVersion,
                string.Join(", ", missing)));
        }

        return messages;
    }
}
