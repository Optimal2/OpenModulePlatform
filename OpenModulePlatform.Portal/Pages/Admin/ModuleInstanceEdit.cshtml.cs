// File: OpenModulePlatform.Portal/Pages/Admin/ModuleInstanceEdit.cshtml.cs
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
/// Edits a module instance.
/// A module instance binds a reusable module definition to a concrete OMP instance.
/// </summary>
public sealed class ModuleInstanceEditModel : OmpPortalPageModel
{
    private static readonly Regex KeyPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$",
        RegexOptions.Compiled);

    private readonly OmpAdminRepository _repo;

    public ModuleInstanceEditModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<OptionItem> InstanceOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> ModuleOptions { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsCreate => Input.ModuleInstanceId == Guid.Empty;

    public async Task<IActionResult> OnGet(Guid? id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles(id.HasValue ? "Edit module instance" : "Create module instance");

        if (!id.HasValue)
        {
            return Page();
        }

        var row = await _repo.GetModuleInstanceAsync(id.Value, ct);
        if (row is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            ModuleInstanceId = row.ModuleInstanceId,
            InstanceId = row.InstanceId,
            ModuleId = row.ModuleId,
            ModuleInstanceKey = row.ModuleInstanceKey,
            DisplayName = row.DisplayName,
            Description = row.Description,
            IsEnabled = row.IsEnabled,
            SortOrder = row.SortOrder
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
        SetTitles(IsCreate ? "Create module instance" : "Edit module instance");

        ValidateInput();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var id = await _repo.SaveModuleInstanceAsync(
                new ModuleInstanceEditData
                {
                    ModuleInstanceId = Input.ModuleInstanceId,
                    InstanceId = Input.InstanceId,
                    ModuleId = Input.ModuleId,
                    ModuleInstanceKey = Input.ModuleInstanceKey.Trim(),
                    DisplayName = Input.DisplayName.Trim(),
                    Description = Clean(Input.Description),
                    IsEnabled = Input.IsEnabled,
                    SortOrder = Input.SortOrder
                },
                ct);

            StatusMessage = IsCreate ? T("Module instance created.") : T("Module instance updated.");
            return RedirectToPage("/Admin/ModuleInstanceEdit", new { id });
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(
                string.Empty,
                ToFriendlySqlMessage(ex, "The module instance could not be saved."));

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
            await _repo.DeleteModuleInstanceAsync(Input.ModuleInstanceId, ct);
            StatusMessage = T("Module instance deleted.");
            return RedirectToPage("/Admin/ModuleInstances");
        }
        catch (SqlException ex)
        {
            await LoadAsync(ct);
            SetTitles("Edit module instance");
            ModelState.AddModelError(
                string.Empty,
                ToFriendlySqlMessage(ex, "The module instance could not be deleted."));

            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        InstanceOptions = await _repo.GetInstanceOptionsAsync(ct);
        ModuleOptions = await _repo.GetModuleOptionsAsync(ct);
    }

    private void ValidateInput()
    {
        if (Input.InstanceId == Guid.Empty)
        {
            ModelState.AddModelError(nameof(Input.InstanceId), "Select an instance.");
        }

        if (Input.ModuleId <= 0)
        {
            ModelState.AddModelError(nameof(Input.ModuleId), "Select a module.");
        }

        if (!KeyPattern.IsMatch(Input.ModuleInstanceKey ?? string.Empty))
        {
            ModelState.AddModelError(
                nameof(Input.ModuleInstanceKey),
                "Use a stable key with letters, digits, dash, underscore or dot.");
        }
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToFriendlySqlMessage(SqlException ex, string fallback)
        => ex.Number switch
        {
            2601 or 2627 => "A module instance with the same key already exists in the selected instance.",
            547 => "Delete or update dependent rows first.",
            _ => fallback
        };

    public sealed class InputModel
    {
        public Guid ModuleInstanceId { get; set; }

        [Required]
        [Display(Name = "Instance")]
        public Guid InstanceId { get; set; }

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

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;

        [Display(Name = "Sort order")]
        public int SortOrder { get; set; }
    }
}
