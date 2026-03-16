// File: OpenModulePlatform.Web.ExampleServiceAppModule/Pages/AppInstances/Edit.cshtml.cs
using OpenModulePlatform.Web.ExampleServiceAppModule.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Web.ExampleServiceAppModule.Pages.AppInstances;

public sealed class EditModel : ExampleServiceAppModulePageModel
{
    private readonly ExampleServiceAppModuleAdminRepository _repo;

    public EditModel(IOptions<WebAppOptions> options, RbacService rbac, ExampleServiceAppModuleAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public EditInput Input { get; set; } = new();

    public string? StatusMessage { get; private set; }

    public async Task<IActionResult> OnGet(Guid appInstanceId, CancellationToken ct)
    {
        var guard = await RequireAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Edit app instance");
        var row = await _repo.GetAppInstanceAsync(appInstanceId, ct);
        if (row is null)
            return NotFound();

        Input = new EditInput
        {
            AppInstanceId = row.AppInstanceId,
            IsAllowed = row.IsAllowed,
            DesiredState = row.DesiredState,
            ConfigId = row.ConfigId,
            ArtifactId = row.ArtifactId
        };

        return Page();
    }

    public async Task<IActionResult> OnPost(CancellationToken ct)
    {
        var guard = await RequireAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Edit app instance");
        await _repo.UpdateAppInstanceAsync(
            Input.AppInstanceId,
            Input.IsAllowed,
            Input.DesiredState,
            Input.ConfigId,
            Input.ArtifactId,
            User?.Identity?.Name ?? "unknown",
            ct);
        StatusMessage = "App instance updated.";
        return Page();
    }

    public sealed class EditInput
    {
        public Guid AppInstanceId { get; set; }

        [Display(Name = "Allowed")]
        public bool IsAllowed { get; set; }

        [Display(Name = "Desired state")]
        public byte DesiredState { get; set; }

        [Display(Name = "ConfigId")]
        public int? ConfigId { get; set; }

        [Display(Name = "ArtifactId")]
        public int? ArtifactId { get; set; }
    }
}
