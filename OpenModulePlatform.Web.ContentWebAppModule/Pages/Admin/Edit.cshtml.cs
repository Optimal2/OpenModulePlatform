// File: OpenModulePlatform.Web.ContentWebAppModule/Pages/Admin/Edit.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Localization;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using OpenModulePlatform.Web.ContentWebAppModule.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Pages;
using OpenModulePlatform.Web.ContentWebAppModule.Services;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Web.ContentWebAppModule.Pages.Admin;

public sealed class EditModel : ContentWebAppModulePageModel
{
    private readonly ContentPageRepository _repo;
    private readonly HtmlContentFileLoader _htmlFileLoader;
    private readonly ServerReportDefinitionLoader _serverReportLoader;
    private readonly ActivityLogWriter _activityLog;

    public EditModel(
        IOptions<WebAppOptions> options,
        IOptions<ContentWebAppModuleOptions> contentOptions,
        RbacService rbac,
        ContentPageRepository repo,
        HtmlContentFileLoader htmlFileLoader,
        ServerReportDefinitionLoader serverReportLoader,
        ActivityLogWriter activityLog)
        : base(options, contentOptions, rbac)
    {
        _repo = repo;
        _htmlFileLoader = htmlFileLoader;
        _serverReportLoader = serverReportLoader;
        _activityLog = activityLog;
    }

    [BindProperty]
    public EditInput Input { get; set; } = new();

    public string? StatusMessage { get; private set; }
    public bool IsCreate => Input.ContentId == Guid.Empty;
    public bool CanManageAll { get; private set; }
    public IReadOnlyList<string> AvailableHtmlFileKeys { get; private set; } = [];
    public IReadOnlyList<string> AvailableServerReportKeys { get; private set; } = [];

    public async Task<IActionResult> OnGet(Guid? contentId, string? saved, CancellationToken ct)
    {
        var guard = await PrepareAsync("Edit page", ct);
        if (guard is not null)
        {
            return guard;
        }

        if (contentId is null || contentId.Value == Guid.Empty)
        {
            if (!CanManageAll)
            {
                return Forbid();
            }

            Input = new EditInput
            {
                IsEnabled = true,
                RoleAccesses = await LoadRoleAccessInputsAsync(Guid.Empty, ct)
            };

            return Page();
        }

        var accessContext = await GetContentAccessContextAsync(ct);
        var row = await _repo.GetPageForEditAsync(
            AppInstanceId,
            contentId.Value,
            accessContext.RoleIds,
            accessContext.CanManageAll,
            ct);

        if (row is null)
        {
            return await _repo.ContentExistsAsync(AppInstanceId, contentId.Value, ct)
                ? Forbid()
                : NotFound();
        }

        Input = FromRow(row);
        Input.RoleAccesses = await LoadRoleAccessInputsAsync(row.ContentId, ct);
        StatusMessage = saved switch
        {
            "1" => T("Page saved."),
            "enabled" => T("Page enabled."),
            "disabled" => T("Page disabled."),
            _ => null
        };
        return Page();
    }

    public async Task<IActionResult> OnPostSave(CancellationToken ct)
    {
        var guard = await PrepareAsync("Edit page", ct);
        if (guard is not null)
        {
            return guard;
        }

        if (Input.ContentId == Guid.Empty && !CanManageAll)
        {
            return Forbid();
        }

        if (Input.ContentId != Guid.Empty)
        {
            var accessContext = await GetContentAccessContextAsync(ct);
            var editableRow = await _repo.GetPageForEditAsync(
                AppInstanceId,
                Input.ContentId,
                accessContext.RoleIds,
                accessContext.CanManageAll,
                ct);

            if (editableRow is null)
            {
                return Forbid();
            }

            Input.ContentType = editableRow.ContentType;
            if (!CanManageAll)
            {
                Input.IsEnabled = editableRow.IsEnabled;
                Input.RoleAccesses = ToRoleAccessInputs(await _repo.ListRoleAccessAsync(Input.ContentId, ct));
            }
        }

        NormalizeInput();
        ValidateInput();

        if (!ModelState.IsValid)
        {
            if (CanManageAll && Input.RoleAccesses.Count == 0)
            {
                Input.RoleAccesses = await LoadRoleAccessInputsAsync(Input.ContentId, ct);
            }

            return Page();
        }

        var isCreate = Input.ContentId == Guid.Empty;
        try
        {
            var contentId = await _repo.SavePageAsync(AppInstanceId, ToSaveRequest(), CurrentUserName(), ct);
            await WritePageEntryAsync(
                isCreate ? "content_page.created" : "content_page.updated",
                isCreate ? $"Created content page \"{Input.Title}\"" : $"Updated content page \"{Input.Title}\"",
                contentId,
                Input.Title,
                Input.Slug);
            return RedirectToPage("/Admin/Edit", new { contentId, saved = "1" });
        }
        catch (InvalidOperationException ex)
        {
            // A slug conflict or similar rule; the reason is shown to the user,
            // never written to the log. A page that was never created has no
            // id to name, so the entry carries no subject then.
            await WritePageEntryAsync(
                isCreate ? "content_page.created" : "content_page.updated",
                isCreate ? $"Could not create content page \"{Input.Title}\"" : $"Could not update content page \"{Input.Title}\"",
                isCreate ? null : Input.ContentId,
                Input.Title,
                Input.Slug,
                ActivityOutcomes.Failed);
            ModelState.AddModelError("Input.Slug", ContentWebAppTextLocalizer.Display(Localizer, ex.Message));
            return Page();
        }
    }

    public async Task<IActionResult> OnPostEnable(CancellationToken ct)
    {
        var guard = await PrepareStateMutationAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        // The row's own title and slug, not the unsaved form text; no row, no
        // entry, since the update then changed nothing.
        var row = await FindPageAsync(ct);
        await _repo.SetEnabledAsync(AppInstanceId, Input.ContentId, isEnabled: true, CurrentUserName(), ct);
        if (row is not null)
        {
            await WritePageEntryAsync("content_page.enabled", $"Enabled content page \"{row.Title}\"", Input.ContentId, row.Title, row.Slug);
        }

        return RedirectToPage("/Admin/Edit", new { contentId = Input.ContentId, saved = "enabled" });
    }

    public async Task<IActionResult> OnPostDisable(CancellationToken ct)
    {
        var guard = await PrepareStateMutationAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var row = await FindPageAsync(ct);
        await _repo.SetEnabledAsync(AppInstanceId, Input.ContentId, isEnabled: false, CurrentUserName(), ct);
        if (row is not null)
        {
            await WritePageEntryAsync("content_page.disabled", $"Disabled content page \"{row.Title}\"", Input.ContentId, row.Title, row.Slug);
        }

        return RedirectToPage("/Admin/Edit", new { contentId = Input.ContentId, saved = "disabled" });
    }

    public async Task<IActionResult> OnPostDelete(CancellationToken ct)
    {
        var guard = await PrepareStateMutationAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        // The delete form posts only the id; the title and slug are read before
        // the row is gone so the log can say which page it was. No row (a stale
        // form, another admin first) means nothing was deleted, and nothing is
        // logged.
        var deletedRow = await FindPageAsync(ct);
        await _repo.DeletePageAsync(AppInstanceId, Input.ContentId, ct);
        if (deletedRow is not null)
        {
            await WritePageEntryAsync("content_page.deleted", $"Deleted content page \"{deletedRow.Title}\"", Input.ContentId, deletedRow.Title, deletedRow.Slug);
        }

        return RedirectToPage("/Admin/Index", new { saved = "deleted" });
    }

    /// <summary>The page the posted id names, as the caller may see it; null when there is no such row.</summary>
    private async Task<ContentPageEditRow?> FindPageAsync(CancellationToken ct)
    {
        var accessContext = await GetContentAccessContextAsync(ct);
        return await _repo.GetPageForEditAsync(AppInstanceId, Input.ContentId, accessContext.RoleIds, accessContext.CanManageAll, ct);
    }

    /// <summary>
    /// One user log entry for a content page. Written after the change has
    /// been committed, so no cancellation token: an aborted request must not
    /// turn a saved change into an error for the admin.
    /// </summary>
    private Task WritePageEntryAsync(
        string eventName,
        string summary,
        Guid? contentId,
        string? title,
        string? slug,
        string outcome = ActivityOutcomes.Ok)
        => _activityLog.WriteAsync(new ActivityEntry
        {
            Event = eventName,
            Summary = summary,
            Outcome = outcome,
            Subject = contentId is { } id
                ? new ActivitySubject("content_page", id.ToString("D"), string.IsNullOrWhiteSpace(title) ? null : title)
                : null,
            Data = new Dictionary<string, object?>
            {
                ["slug"] = string.IsNullOrWhiteSpace(slug) ? null : slug
            }
        }, User, CancellationToken.None);

    private async Task<IActionResult?> PrepareAsync(string title, CancellationToken ct)
    {
        var appInstanceGuard = ValidateAppInstanceConfigured();
        if (appInstanceGuard is not null)
        {
            return appInstanceGuard;
        }

        var accessContext = await GetContentAccessContextAsync(ct);
        CanManageAll = accessContext.CanManageAll;
        AvailableHtmlFileKeys = _htmlFileLoader.ListHtmlFileKeys();
        AvailableServerReportKeys = _serverReportLoader.ListReportKeys();

        await SetContentTitlesAsync(title, ct);
        return null;
    }

    private async Task<IActionResult?> PrepareStateMutationAsync(CancellationToken ct)
    {
        var guard = await PrepareAsync("Edit page", ct);
        if (guard is not null)
        {
            return guard;
        }

        if (!CanManageAll)
        {
            return Forbid();
        }

        if (Input.ContentId == Guid.Empty)
        {
            return BadRequest();
        }

        return null;
    }

    private async Task<List<RoleAccessInput>> LoadRoleAccessInputsAsync(Guid contentId, CancellationToken ct)
    {
        if (contentId == Guid.Empty)
        {
            return ToRoleAccessInputs(await _repo.ListEmptyRoleAccessAsync(ct));
        }

        return ToRoleAccessInputs(await _repo.ListRoleAccessAsync(contentId, ct));
    }

    private static List<RoleAccessInput> ToRoleAccessInputs(IReadOnlyList<ContentRoleAccessRow> rows)
    {
        return rows
            .Select(row => new RoleAccessInput
            {
                RoleId = row.RoleId,
                RoleName = row.RoleName,
                CanRead = row.CanRead || row.CanWrite,
                CanWrite = row.CanWrite
            })
            .ToList();
    }

    private void NormalizeInput()
    {
        Input.Slug = ContentSlugNormalizer.Normalize(Input.Slug);
        Input.Title = Input.Title?.Trim() ?? string.Empty;
        Input.ContentType = ContentTypes.Normalize(Input.ContentType);
        Input.Body ??= string.Empty;
        Input.HtmlFileKey = string.IsNullOrWhiteSpace(Input.HtmlFileKey)
            ? null
            : Input.HtmlFileKey.Trim();
        Input.ServerReportKey = string.IsNullOrWhiteSpace(Input.ServerReportKey)
            ? null
            : Input.ServerReportKey.Trim();

        if (Input.ContentType == ContentTypes.ServerReport)
        {
            Input.Body = string.Empty;
            Input.HtmlFileKey = null;
        }
        else if (Input.ContentType == ContentTypes.HtmlFile)
        {
            Input.Body = string.Empty;
            Input.ServerReportKey = null;
        }
        else
        {
            Input.HtmlFileKey = null;
            Input.ServerReportKey = null;
        }
    }

    private void ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(Input.Slug))
        {
            ModelState.AddModelError("Input.Slug", T("Slug is required."));
        }

        if (string.IsNullOrWhiteSpace(Input.Title))
        {
            ModelState.AddModelError("Input.Title", T("The Title field is required."));
        }

        if (Input.ContentType == ContentTypes.ServerReport)
        {
            if (string.IsNullOrWhiteSpace(Input.ServerReportKey))
            {
                ModelState.AddModelError("Input.ServerReportKey", T("Server report key is required."));
            }
            else if (!_serverReportLoader.IsValidReportKey(Input.ServerReportKey))
            {
                ModelState.AddModelError("Input.ServerReportKey", T("Server report key may only contain letters, numbers, underscores, and hyphens."));
            }
            else if (!_serverReportLoader.ReportExists(Input.ServerReportKey))
            {
                ModelState.AddModelError("Input.ServerReportKey", T("Server report definition was not found."));
            }
        }
        else if (Input.ContentType == ContentTypes.HtmlFile)
        {
            if (string.IsNullOrWhiteSpace(Input.HtmlFileKey))
            {
                ModelState.AddModelError("Input.HtmlFileKey", T("HTML file key is required."));
            }
            else if (!_htmlFileLoader.IsValidHtmlFileKey(Input.HtmlFileKey))
            {
                ModelState.AddModelError("Input.HtmlFileKey", T("HTML file key may only contain letters, numbers, underscores, and hyphens."));
            }
            else if (!_htmlFileLoader.HtmlFileExists(Input.HtmlFileKey))
            {
                ModelState.AddModelError("Input.HtmlFileKey", T("HTML file was not found."));
            }
        }
        else if (string.IsNullOrWhiteSpace(Input.Body))
        {
            ModelState.AddModelError("Input.Body", T("The Content field is required."));
        }
    }

    private ContentPageSaveRequest ToSaveRequest()
    {
        return new ContentPageSaveRequest
        {
            ContentId = Input.ContentId,
            Slug = Input.Slug,
            Title = Input.Title,
            ContentType = Input.ContentType,
            Body = Input.Body ?? string.Empty,
            ServerReportKey = Input.ServerReportKey,
            HtmlFileKey = Input.HtmlFileKey,
            IsEnabled = Input.IsEnabled,
            SortOrder = Input.SortOrder,
            RoleAccesses = Input.RoleAccesses
                .Select(x => new ContentRoleAccessSaveRow
                {
                    RoleId = x.RoleId,
                    CanRead = x.CanRead || x.CanWrite,
                    CanWrite = x.CanWrite
                })
                .ToArray(),
            SaveRoleAccess = CanManageAll
        };
    }

    private static EditInput FromRow(ContentPageEditRow row)
    {
        return new EditInput
        {
            ContentId = row.ContentId,
            Slug = row.Slug,
            Title = row.Title,
            ContentType = row.ContentType,
            Body = row.Body,
            HtmlFileKey = row.ContentType == ContentTypes.HtmlFile ? row.ServerReportKey : null,
            ServerReportKey = row.ContentType == ContentTypes.ServerReport ? row.ServerReportKey : null,
            IsEnabled = row.IsEnabled,
            SortOrder = row.SortOrder,
            CreatedAtUtc = row.CreatedAtUtc,
            CreatedBy = row.CreatedBy,
            UpdatedAtUtc = row.UpdatedAtUtc,
            UpdatedBy = row.UpdatedBy
        };
    }

    public sealed class EditInput
    {
        public Guid ContentId { get; set; }

        [Required]
        [Display(Name = "Slug")]
        [StringLength(256)]
        public string Slug { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Title")]
        [StringLength(200)]
        public string Title { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Content type")]
        public string ContentType { get; set; } = ContentTypes.Markdown;

        [Display(Name = "Content")]
        public string? Body { get; set; } = string.Empty;

        [Display(Name = "Server report")]
        [StringLength(128)]
        public string? ServerReportKey { get; set; }

        [Display(Name = "HTML file")]
        [StringLength(128)]
        public string? HtmlFileKey { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;

        [Display(Name = "Sort order")]
        public int? SortOrder { get; set; }

        public DateTime? CreatedAtUtc { get; set; }

        public string? CreatedBy { get; set; }

        public DateTime? UpdatedAtUtc { get; set; }

        public string? UpdatedBy { get; set; }

        public List<RoleAccessInput> RoleAccesses { get; set; } = [];
    }

    public sealed class RoleAccessInput
    {
        public int RoleId { get; set; }

        public string RoleName { get; set; } = string.Empty;

        public bool CanRead { get; set; }

        public bool CanWrite { get; set; }
    }
}
