// File: OpenModulePlatform.Web.ExampleWorkerAppModule/Pages/AppInstances/Edit.cshtml.cs
using OpenModulePlatform.Web.ExampleWorkerAppModule.Services;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Configuration;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Web.ExampleWorkerAppModule.Pages.AppInstances;

public sealed class EditModel : ExampleWorkerAppModulePageModel
{
    private readonly ExampleWorkerAppModuleAdminRepository _repo;
    private readonly ActivityLogWriter _activityLog;

    public EditModel(IOptions<WebAppOptions> options, RbacService rbac, ExampleWorkerAppModuleAdminRepository repo, ActivityLogWriter activityLog)
        : base(options, rbac)
    {
        _repo = repo;
        _activityLog = activityLog;
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
        var updated = await _repo.UpdateAppInstanceAsync(
            Input.AppInstanceId,
            Input.IsAllowed,
            Input.DesiredState,
            Input.ConfigId,
            Input.ArtifactId,
            User?.Identity?.Name ?? "unknown",
            ct);
        if (!updated)
        {
            return NotFound();
        }

        // What the admin set the instance to: allowed or not, the desired
        // state and which configuration and artifact it points at. These are
        // deployment facts, not secrets, and they are what a reviewer wants.
        if (OmpUserIdentity.TryGetOmpUserId(User) is not null)
        {
            await _activityLog.WriteAsync(new ActivityEntry
            {
                Event = "app_instance.saved",
                Summary = $"Saved app instance {Input.AppInstanceId:D} (allowed: {Input.IsAllowed}, desired state: {Input.DesiredState})",
                Subject = new ActivitySubject("app_instance", Input.AppInstanceId.ToString("D")),
                Data = new Dictionary<string, object?>
                {
                    ["isAllowed"] = Input.IsAllowed,
                    ["desiredState"] = Input.DesiredState,
                    ["configId"] = Input.ConfigId?.Value,
                    ["artifactId"] = Input.ArtifactId
                }
            }, User, CancellationToken.None);
        }

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
        public ModuleConfigId? ConfigId { get; set; }

        [Display(Name = "ArtifactId")]
        public int? ArtifactId { get; set; }
    }
}
