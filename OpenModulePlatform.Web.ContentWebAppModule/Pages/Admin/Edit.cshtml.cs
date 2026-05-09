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

    public IReadOnlyList<ContentPageRevisionRow> Revisions { get; private set; } = [];
    public string? StatusMessage { get; private set; }
    public bool IsCreate => Input.PageId == Guid.Empty;

    public async Task<IActionResult> OnGet(Guid? pageId, string? saved, CancellationToken ct)
    {
        var guard = await PrepareAsync("Edit page", ct);
        if (guard is not null)
        {
            return guard;
        }

        if (pageId is null || pageId.Value == Guid.Empty)
        {
            Input = new EditInput();
            StatusMessage = saved switch
            {
                "published" => T("Page published."),
                "unpublished" => T("Page unpublished."),
                _ => null
            };
            return Page();
        }

        var row = await _repo.GetPageForEditAsync(AppInstanceId, pageId.Value, ct);
        if (row is null)
        {
            return NotFound();
        }

        Input = FromRow(row);
        Revisions = await _repo.ListRevisionsAsync(row.PageId, ct);
        StatusMessage = saved switch
        {
            "1" => T("Page saved."),
            "published" => T("Page published."),
            "unpublished" => T("Page unpublished."),
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

        NormalizeInput();
        if (!ModelState.IsValid)
        {
            await LoadRevisionsAsync(ct);
            return Page();
        }

        try
        {
            var pageId = await _repo.SavePageAsync(AppInstanceId, ToSaveRequest(), CurrentUserName(), ct);
            return RedirectToPage("/Admin/Edit", new { pageId, saved = "1" });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(nameof(Input.Slug), T(ex.Message));
            await LoadRevisionsAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostPublish(CancellationToken ct)
    {
        var guard = await PrepareMutationAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await _repo.PublishPageAsync(AppInstanceId, Input.PageId, CurrentUserName(), ct);
        return RedirectToPage("/Admin/Edit", new { pageId = Input.PageId, saved = "published" });
    }

    public async Task<IActionResult> OnPostUnpublish(CancellationToken ct)
    {
        var guard = await PrepareMutationAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await _repo.UnpublishPageAsync(AppInstanceId, Input.PageId, CurrentUserName(), ct);
        return RedirectToPage("/Admin/Edit", new { pageId = Input.PageId, saved = "unpublished" });
    }

    public async Task<IActionResult> OnPostDelete(CancellationToken ct)
    {
        var guard = await PrepareMutationAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await _repo.SoftDeletePageAsync(AppInstanceId, Input.PageId, CurrentUserName(), ct);
        return RedirectToPage("/Admin/Index");
    }

    private async Task<IActionResult?> PrepareAsync(string title, CancellationToken ct)
    {
        var guard = await RequireManageAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var appInstanceGuard = ValidateAppInstanceConfigured();
        if (appInstanceGuard is not null)
        {
            return appInstanceGuard;
        }

        await SetContentTitlesAsync(title, ct);
        return null;
    }

    private async Task<IActionResult?> PrepareMutationAsync(CancellationToken ct)
    {
        var guard = await PrepareAsync("Edit page", ct);
        if (guard is not null)
        {
            return guard;
        }

        if (Input.PageId == Guid.Empty)
        {
            return BadRequest();
        }

        return null;
    }

    private async Task LoadRevisionsAsync(CancellationToken ct)
    {
        Revisions = Input.PageId == Guid.Empty
            ? []
            : await _repo.ListRevisionsAsync(Input.PageId, ct);
    }

    private void NormalizeInput()
    {
        Input.Slug = ContentSlugNormalizer.Normalize(Input.Slug);
        Input.ContentFormat = ContentFormats.Normalize(Input.ContentFormat);
    }

    private ContentPageSaveRequest ToSaveRequest()
    {
        return new ContentPageSaveRequest
        {
            PageId = Input.PageId,
            Slug = Input.Slug,
            Title = Input.Title,
            Summary = Input.Summary,
            MetaTitle = Input.MetaTitle,
            MetaDescription = Input.MetaDescription,
            ContentFormat = Input.ContentFormat,
            Content = Input.Content,
            SortOrder = Input.SortOrder,
            ChangeNote = Input.ChangeNote
        };
    }

    private static EditInput FromRow(ContentPageEditRow row)
    {
        return new EditInput
        {
            PageId = row.PageId,
            Slug = row.Slug,
            Title = row.Title,
            Summary = row.Summary,
            MetaTitle = row.MetaTitle,
            MetaDescription = row.MetaDescription,
            ContentFormat = row.ContentFormat,
            Content = row.Content,
            IsPublished = row.IsPublished,
            PublishedAtUtc = row.PublishedAtUtc,
            SortOrder = row.SortOrder
        };
    }

    public sealed class EditInput
    {
        public Guid PageId { get; set; }

        [Display(Name = "Slug")]
        [StringLength(256)]
        public string Slug { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Title")]
        [StringLength(200)]
        public string Title { get; set; } = string.Empty;

        [Display(Name = "Summary")]
        [StringLength(500)]
        public string? Summary { get; set; }

        [Display(Name = "Meta title")]
        [StringLength(200)]
        public string? MetaTitle { get; set; }

        [Display(Name = "Meta description")]
        [StringLength(500)]
        public string? MetaDescription { get; set; }

        [Required]
        [Display(Name = "Content format")]
        public string ContentFormat { get; set; } = ContentFormats.Markdown;

        [Required]
        [Display(Name = "Content")]
        public string Content { get; set; } = string.Empty;

        [Display(Name = "Sort order")]
        public int SortOrder { get; set; }

        [Display(Name = "Change note")]
        [StringLength(500)]
        public string? ChangeNote { get; set; }

        public bool IsPublished { get; set; }
        public DateTime? PublishedAtUtc { get; set; }
    }
}
