// File: OpenModulePlatform.Web.ContentWebAppModule/Pages/Admin/Edit.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using OpenModulePlatform.Web.ContentWebAppModule.Options;
using OpenModulePlatform.Web.ContentWebAppModule.Pages;
using OpenModulePlatform.Web.ContentWebAppModule.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Web.ContentWebAppModule.Pages.Admin;

public sealed class EditModel : ContentWebAppModulePageModel
{
    private readonly ContentPageRepository _repo;

    public EditModel(
        IOptions<WebAppOptions> options,
        IOptions<ContentWebAppModuleOptions> contentOptions,
        RbacService rbac,
        ContentPageRepository repo)
        : base(options, contentOptions, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public EditInput Input { get; set; } = new();

    public string? StatusMessage { get; private set; }
    public bool IsCreate => Input.ContentId == Guid.Empty;
    public bool CanManageAll { get; private set; }

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

        if (Input.ContentId != Guid.Empty && !CanManageAll)
        {
            var accessContext = await GetContentAccessContextAsync(ct);
            var editableRow = await _repo.GetPageForEditAsync(
                AppInstanceId,
                Input.ContentId,
                accessContext.RoleIds,
                canManageAll: false,
                ct);

            if (editableRow is null)
            {
                return Forbid();
            }

            Input.IsEnabled = editableRow.IsEnabled;
            Input.RoleAccesses = ToRoleAccessInputs(await _repo.ListRoleAccessAsync(Input.ContentId, ct));
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

        try
        {
            var contentId = await _repo.SavePageAsync(AppInstanceId, ToSaveRequest(), CurrentUserName(), ct);
            return RedirectToPage("/Admin/Edit", new { contentId, saved = "1" });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("Input.Slug", T(ex.Message));
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

        await _repo.SetEnabledAsync(AppInstanceId, Input.ContentId, isEnabled: true, CurrentUserName(), ct);
        return RedirectToPage("/Admin/Edit", new { contentId = Input.ContentId, saved = "enabled" });
    }

    public async Task<IActionResult> OnPostDisable(CancellationToken ct)
    {
        var guard = await PrepareStateMutationAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await _repo.SetEnabledAsync(AppInstanceId, Input.ContentId, isEnabled: false, CurrentUserName(), ct);
        return RedirectToPage("/Admin/Edit", new { contentId = Input.ContentId, saved = "disabled" });
    }

    private async Task<IActionResult?> PrepareAsync(string title, CancellationToken ct)
    {
        var appInstanceGuard = ValidateAppInstanceConfigured();
        if (appInstanceGuard is not null)
        {
            return appInstanceGuard;
        }

        var accessContext = await GetContentAccessContextAsync(ct);
        CanManageAll = accessContext.CanManageAll;

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

        if (string.IsNullOrWhiteSpace(Input.Body))
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
            Body = Input.Body,
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

        [Required]
        [Display(Name = "Content")]
        public string Body { get; set; } = string.Empty;

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
