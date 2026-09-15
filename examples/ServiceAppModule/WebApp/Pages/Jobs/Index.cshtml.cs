// File: OpenModulePlatform.Web.ExampleServiceAppModule/Pages/Jobs/Index.cshtml.cs
using OpenModulePlatform.Web.ExampleServiceAppModule.Services;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Security;
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
    private readonly ActivityLogWriter _activityLog;

    public IndexModel(IOptions<WebAppOptions> options, RbacService rbac, ExampleServiceAppModuleAdminRepository repo, ActivityLogWriter activityLog)
        : base(options, rbac)
    {
        _repo = repo;
        _activityLog = activityLog;
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
        var jobId = await _repo.EnqueueJobAsync(Input.RequestType, Input.PayloadJson, User?.Identity?.Name ?? "unknown", ct);
        // A person queued a job with a click: that is the user log's business.
        // The job running later is the module's own history (Jobs and
        // JobExecutions), not the log's. The request type is a fixed vocabulary;
        // the payload is not logged.
        if (OmpUserIdentity.TryGetOmpUserId(User) is not null)
        {
            await _activityLog.WriteAsync(new ActivityEntry
            {
                Event = "job.queued",
                Summary = $"Queued job {jobId} ({Input.RequestType})",
                Subject = new ActivitySubject("job", jobId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Data = new Dictionary<string, object?> { ["requestType"] = Input.RequestType }
            }, User, CancellationToken.None);
        }

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
