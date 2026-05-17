// File: OpenModulePlatform.Portal/Pages/Admin/InstanceTemplateModuleEdit.cshtml.cs
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// Edits a desired module-instance row on an instance template.
/// </summary>
public sealed class InstanceTemplateModuleEditModel : OmpPortalPageModel
{
    private static readonly Regex KeyPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$",
        RegexOptions.Compiled);

    private readonly OmpAdminRepository _repo;

    public InstanceTemplateModuleEditModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public InstanceTemplateRow? Template { get; private set; }

    public IReadOnlyList<OptionItem> ModuleOptions { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsCreate => Input.InstanceTemplateModuleInstanceId == 0;

    public async Task<IActionResult> OnGet(int? id, int? templateId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (id.HasValue)
        {
            var row = await _repo.GetInstanceTemplateModuleAsync(id.Value, ct);
            if (row is null)
            {
                return NotFound();
            }

            Input = ToInput(row);
        }
        else
        {
            if (!templateId.HasValue)
            {
                return BadRequest();
            }

            Input.InstanceTemplateId = templateId.Value;
            Input.IsEnabled = true;
        }

        await LoadAsync(ct);
        SetTitles(IsCreate ? "Add template module instance" : "Edit template module instance");
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
        SetTitles(IsCreate ? "Add template module instance" : "Edit template module instance");

        ValidateInput();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var id = await _repo.SaveInstanceTemplateModuleAsync(
                new InstanceTemplateModuleEditData
                {
                    InstanceTemplateModuleInstanceId = Input.InstanceTemplateModuleInstanceId,
                    InstanceTemplateId = Input.InstanceTemplateId,
                    ModuleId = Input.ModuleId,
                    ModuleInstanceKey = Input.ModuleInstanceKey.Trim(),
                    DisplayName = Input.DisplayName.Trim(),
                    Description = Clean(Input.Description),
                    SortOrder = Input.SortOrder,
                    IsEnabled = Input.IsEnabled
                },
                ct);

            StatusMessage = IsCreate ? T("Template module instance added.") : T("Template module instance updated.");
            return RedirectToPage("/Admin/InstanceTemplateModuleEdit", new { id });
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(string.Empty, T(ToFriendlySqlMessage(ex)));
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

        var templateId = Input.InstanceTemplateId;

        try
        {
            await _repo.DeleteInstanceTemplateModuleAsync(Input.InstanceTemplateModuleInstanceId, ct);
            StatusMessage = T("Template module instance removed.");
            return RedirectToPage("/Admin/InstanceTemplateEdit", new { id = templateId });
        }
        catch (SqlException ex)
        {
            await LoadAsync(ct);
            SetTitles("Edit template module instance");
            ModelState.AddModelError(string.Empty, T(ToFriendlySqlMessage(ex)));
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Template = await _repo.GetInstanceTemplateAsync(Input.InstanceTemplateId, ct);
        if (Template is null)
        {
            ModelState.AddModelError(string.Empty, T("Instance template was not found."));
        }

        ModuleOptions = await _repo.GetModuleOptionsAsync(ct);
    }

    private void ValidateInput()
    {
        if (Input.InstanceTemplateId <= 0)
        {
            ModelState.AddModelError(nameof(Input.InstanceTemplateId), T("Select an instance template."));
        }

        if (Input.ModuleId <= 0)
        {
            ModelState.AddModelError(nameof(Input.ModuleId), T("Select a module."));
        }

        if (!KeyPattern.IsMatch(Input.ModuleInstanceKey ?? string.Empty))
        {
            ModelState.AddModelError(
                nameof(Input.ModuleInstanceKey),
                T("Use a stable key with letters, digits, dash, underscore or dot."));
        }
    }

    private static InputModel ToInput(InstanceTemplateModuleEditData row)
        => new()
        {
            InstanceTemplateModuleInstanceId = row.InstanceTemplateModuleInstanceId,
            InstanceTemplateId = row.InstanceTemplateId,
            ModuleId = row.ModuleId,
            ModuleInstanceKey = row.ModuleInstanceKey,
            DisplayName = row.DisplayName,
            Description = row.Description,
            SortOrder = row.SortOrder,
            IsEnabled = row.IsEnabled
        };

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToFriendlySqlMessage(SqlException ex)
        => ex.Number switch
        {
            2601 or 2627 => "A template module instance with the same key already exists in this template.",
            547 => "Delete desired app rows that use this template module instance first.",
            _ => "The template module instance could not be saved."
        };

    public sealed class InputModel
    {
        public int InstanceTemplateModuleInstanceId { get; set; }

        public int InstanceTemplateId { get; set; }

        [Required]
        [Display(Name = "Module")]
        public int ModuleId { get; set; }

        [Required]
        [StringLength(100)]
        [Display(Name = "Module instance key")]
        public string ModuleInstanceKey { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Description { get; set; }

        [Display(Name = "Sort order")]
        public int SortOrder { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;
    }
}
