// File: OpenModulePlatform.Portal/Pages/Admin/InstanceTemplateEdit.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Shows the desired topology stored in an instance template.
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

    public async Task<IActionResult> OnGet(int id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        Template = await _repo.GetInstanceTemplateAsync(id, ct);
        if (Template is null)
        {
            return NotFound();
        }

        Hosts = await _repo.GetInstanceTemplateHostsAsync(id, ct);
        Modules = await _repo.GetInstanceTemplateModulesAsync(id, ct);
        Apps = await _repo.GetInstanceTemplateAppsAsync(id, ct);

        SetTitles("Instance template");
        return Page();
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
        catch (SqlException)
        {
            StatusMessage = T("The desired app could not be removed. Delete or update dependent rows first.");
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
            var version = await _repo.UpgradeInstanceTemplateAppArtifactAsync(id, artifactId, ct);
            StatusMessage = string.Format(T("Desired artifact updated to version {0}."), version);
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = T(ex.Message);
        }
        catch (SqlException ex)
        {
            StatusMessage = string.Format(T("The desired artifact could not be updated: {0}"), ex.Message);
        }

        return RedirectToPage("/Admin/InstanceTemplateEdit", new { id = templateId });
    }
}
