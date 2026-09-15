// File: OpenModulePlatform.Web.ExampleWebAppModule/Pages/Configurations/Edit.cshtml.cs
using OpenModulePlatform.Web.ExampleWebAppModule.Services;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Configuration;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Web.ExampleWebAppModule.Pages.Configurations;

public sealed class EditModel : ExampleWebAppModulePageModel
{
    private readonly ExampleWebAppModuleAdminRepository _repo;
    private readonly ActivityLogWriter _activityLog;

    public EditModel(IOptions<WebAppOptions> options, RbacService rbac, ExampleWebAppModuleAdminRepository repo, ActivityLogWriter activityLog)
        : base(options, rbac)
    {
        _repo = repo;
        _activityLog = activityLog;
    }

    [BindProperty]
    public EditInput Input { get; set; } = new();

    public string? StatusMessage { get; private set; }

    public async Task<IActionResult> OnGet(int configId, CancellationToken ct)
    {
        var guard = await RequireAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Edit configuration");
        var row = await _repo.GetConfigurationAsync(new ModuleConfigId(configId), ct);
        if (row is null)
            return NotFound();

        Input = new EditInput
        {
            ConfigId = row.ConfigId,
            Comment = row.Comment,
            ConfigJson = row.ConfigJson
        };

        return Page();
    }

    public async Task<IActionResult> OnPost(CancellationToken ct)
    {
        var guard = await RequireAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Edit configuration");
        var updated = await _repo.UpdateConfigurationAsync(Input.ConfigId, Input.ConfigJson, Input.Comment, User?.Identity?.Name ?? "unknown", ct);
        if (!updated)
        {
            // The same answer the GET gives for an id that is not there.
            return NotFound();
        }

        // The user log says that the configuration was saved and which one,
        // never what it contains or what the comment said. Written after the
        // change has committed, so no cancellation token; and only for a
        // signed-in OMP user, since an anonymous save (development mode) is
        // not a person the log can name.
        if (OmpUserIdentity.TryGetOmpUserId(User) is not null)
        {
            await _activityLog.WriteAsync(new ActivityEntry
            {
                Event = "configuration.saved",
                Summary = $"Saved configuration {Input.ConfigId.Value}",
                Subject = new ActivitySubject("configuration", Input.ConfigId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            }, User, CancellationToken.None);
        }

        StatusMessage = T("Configuration updated.");
        return Page();
    }

    public sealed class EditInput
    {
        public ModuleConfigId ConfigId { get; set; }

        [Display(Name = "Comment")]
        public string? Comment { get; set; }

        [Required]
        [Display(Name = "Config JSON")]
        public string ConfigJson { get; set; } = "{}";
    }
}
