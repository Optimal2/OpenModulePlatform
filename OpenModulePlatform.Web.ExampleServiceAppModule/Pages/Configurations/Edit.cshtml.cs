// File: OpenModulePlatform.Web.ExampleServiceAppModule/Pages/Configurations/Edit.cshtml.cs
using OpenModulePlatform.Web.ExampleServiceAppModule.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Web.ExampleServiceAppModule.Pages.Configurations;

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

    public async Task<IActionResult> OnGet(int configId, CancellationToken ct)
    {
        var guard = await RequireAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Edit configuration");
        var row = await _repo.GetConfigurationAsync(configId, ct);
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
        await _repo.UpdateConfigurationAsync(Input.ConfigId, Input.ConfigJson, Input.Comment, User?.Identity?.Name ?? "unknown", ct);
        StatusMessage = "Configuration updated.";
        return Page();
    }

    public sealed class EditInput
    {
        public int ConfigId { get; set; }

        [Display(Name = "Comment")]
        public string? Comment { get; set; }

        [Required]
        [Display(Name = "Config JSON")]
        public string ConfigJson { get; set; } = "{}";
    }
}
