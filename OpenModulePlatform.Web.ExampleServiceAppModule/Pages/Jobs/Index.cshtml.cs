// File: OpenModulePlatform.Web.ExampleServiceAppModule/Pages/Jobs/Index.cshtml.cs
using OpenModulePlatform.Web.ExampleServiceAppModule.Services;
using OpenModulePlatform.Web.ExampleServiceAppModule.ViewModels;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Web.ExampleServiceAppModule.Pages.Jobs;

public sealed class IndexModel : ExampleServiceAppModulePageModel
{
    private readonly ExampleServiceAppModuleAdminRepository _repo;

    public IndexModel(IOptions<WebAppOptions> options, RbacService rbac, ExampleServiceAppModuleAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public JobInput Input { get; set; } = new();

    public IReadOnlyList<JobRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequireViewAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Jobs");
        Rows = await _repo.GetJobsAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPost(CancellationToken ct)
    {
        var guard = await RequireAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Jobs");
        await _repo.EnqueueJobAsync(Input.RequestType, Input.PayloadJson, User?.Identity?.Name ?? "unknown", ct);
        Rows = await _repo.GetJobsAsync(ct);
        return Page();
    }

    public sealed class JobInput
    {
        [Required]
        [Display(Name = "Request type")]
        public string RequestType { get; set; } = "sample.run";

        [Required]
        [Display(Name = "Payload JSON")]
        public string PayloadJson { get; set; } = "{}";
    }
}
