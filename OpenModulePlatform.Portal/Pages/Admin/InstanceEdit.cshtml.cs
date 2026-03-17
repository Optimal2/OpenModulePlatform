// File: OpenModulePlatform.Portal/Pages/Admin/InstanceEdit.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Edits a top-level OMP instance.
/// The page intentionally treats template linkage as optional because manual setup is a first-class scenario.
/// </summary>
public sealed class InstanceEditModel : OmpPortalPageModel
{
    private static readonly Regex KeyPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$",
        RegexOptions.Compiled);

    private readonly OmpAdminRepository _repo;

    public InstanceEditModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<OptionItem> InstanceTemplateOptions { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsCreate => Input.InstanceId == Guid.Empty;

    public async Task<IActionResult> OnGet(Guid? id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles(id.HasValue ? "Edit instance" : "Create instance");

        if (!id.HasValue)
        {
            return Page();
        }

        var row = await _repo.GetInstanceAsync(id.Value, ct);
        if (row is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            InstanceId = row.InstanceId,
            InstanceKey = row.InstanceKey,
            DisplayName = row.DisplayName,
            Description = row.Description,
            InstanceTemplateId = row.InstanceTemplateId,
            IsEnabled = row.IsEnabled
        };

        return Page();
    }

    public async Task<IActionResult> OnPost(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles(IsCreate ? "Create instance" : "Edit instance");

        ValidateInput();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var id = await _repo.SaveInstanceAsync(
                new InstanceEditData
                {
                    InstanceId = Input.InstanceId,
                    InstanceKey = Input.InstanceKey.Trim(),
                    DisplayName = Input.DisplayName.Trim(),
                    Description = Clean(Input.Description),
                    InstanceTemplateId = Input.InstanceTemplateId,
                    IsEnabled = Input.IsEnabled
                },
                ct);

            StatusMessage = IsCreate ? "Instance created." : "Instance updated.";
            return RedirectToPage("/Admin/InstanceEdit", new { id });
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(
                string.Empty,
                ToFriendlySqlMessage(ex, "The instance could not be saved."));

            return Page();
        }
    }

    public async Task<IActionResult> OnPostDelete(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        try
        {
            await _repo.DeleteInstanceAsync(Input.InstanceId, ct);
            StatusMessage = "Instance deleted.";
            return RedirectToPage("/Admin/Instances");
        }
        catch (SqlException ex)
        {
            await LoadAsync(ct);
            SetTitles("Edit instance");
            ModelState.AddModelError(
                string.Empty,
                ToFriendlySqlMessage(ex, "The instance could not be deleted."));

            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        InstanceTemplateOptions = await _repo.GetInstanceTemplateOptionsAsync(ct);
    }

    private void ValidateInput()
    {
        if (!KeyPattern.IsMatch(Input.InstanceKey ?? string.Empty))
        {
            ModelState.AddModelError(
                nameof(Input.InstanceKey),
                "Use a stable key with letters, digits, dash, underscore or dot.");
        }

        if (string.IsNullOrWhiteSpace(Input.DisplayName))
        {
            ModelState.AddModelError(nameof(Input.DisplayName), "Display name is required.");
        }
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToFriendlySqlMessage(SqlException ex, string fallback)
        => ex.Number switch
        {
            2601 or 2627 => "A row with the same key already exists.",
            547 => "Delete or update dependent rows first.",
            _ => fallback
        };

    public sealed class InputModel
    {
        public Guid InstanceId { get; set; }

        [Required]
        [StringLength(100)]
        [Display(Name = "Instance key")]
        public string InstanceKey { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Description { get; set; }

        [Display(Name = "Instance template")]
        public int? InstanceTemplateId { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;
    }
}
