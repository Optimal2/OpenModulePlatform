// File: OpenModulePlatform.Portal/Pages/Admin/AppEdit.cshtml.cs
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
/// Edits an app definition.
/// App definitions stay reusable across many module instances and app instances.
/// </summary>
public sealed class AppEditModel : OmpPortalPageModel
{
    private static readonly Regex KeyPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$",
        RegexOptions.Compiled);

    private readonly OmpAdminRepository _repo;

    public AppEditModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<OptionItem> ModuleOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> AppTypeOptions =>
    [
        Opt("Portal", T("Portal")),
        Opt("WebApp", T("Web app")),
        Opt("ServiceApp", T("Service app"))
    ];

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsCreate => Input.AppId == 0;

    public async Task<IActionResult> OnGet(int? id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles(id.HasValue ? "Edit app" : "Create app");

        if (!id.HasValue)
        {
            Input.AppType = "WebApp";
            Input.IsEnabled = true;
            return Page();
        }

        var row = await _repo.GetAppAsync(id.Value, ct);
        if (row is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            AppId = row.AppId,
            ModuleId = row.ModuleId,
            AppKey = row.AppKey,
            DisplayName = row.DisplayName,
            AppType = row.AppType,
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
        SetTitles(IsCreate ? "Create app" : "Edit app");

        ValidateInput();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var id = await _repo.SaveAppAsync(
                new AppEditData
                {
                    AppId = Input.AppId,
                    ModuleId = Input.ModuleId,
                    AppKey = Input.AppKey.Trim(),
                    DisplayName = Input.DisplayName.Trim(),
                    AppType = Input.AppType,
                    Description = Clean(Input.Description),
                    IsEnabled = Input.IsEnabled,
                    SortOrder = Input.SortOrder
                },
                ct);

            StatusMessage = IsCreate ? T("App created.") : T("App updated.");
            return RedirectToPage("/Admin/AppEdit", new { id });
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The app could not be saved.")));

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
            await _repo.DeleteAppAsync(Input.AppId, ct);
            StatusMessage = T("App deleted.");
            return RedirectToPage("/Admin/Apps");
        }
        catch (SqlException ex)
        {
            await LoadAsync(ct);
            SetTitles("Edit app");
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The app could not be deleted.")));

            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        ModuleOptions = await _repo.GetModuleOptionsAsync(ct);
    }

    private void ValidateInput()
    {
        if (Input.ModuleId <= 0)
        {
            ModelState.AddModelError(nameof(Input.ModuleId), T("Select a module."));
        }

        if (!KeyPattern.IsMatch(Input.AppKey ?? string.Empty))
        {
            ModelState.AddModelError(
                nameof(Input.AppKey), T("Use a stable key with letters, digits, dash, underscore or dot."));
        }

        if (string.IsNullOrWhiteSpace(Input.AppType))
        {
            ModelState.AddModelError(nameof(Input.AppType), T("Select an app type."));
        }
    }

    private static OptionItem Opt(string value, string label)
        => new() { Value = value, Label = label };

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToFriendlySqlMessage(SqlException ex, string fallback)
        => ex.Number switch
        {
            2601 or 2627 => "An app with the same key already exists in the selected module.",
            547 => "Delete or update dependent rows first.",
            _ => fallback
        };

    public sealed class InputModel
    {
        public int AppId { get; set; }

        [Required]
        [Display(Name = "Module")]
        public int ModuleId { get; set; }

        [Required]
        [StringLength(100)]
        [Display(Name = "App key")]
        public string AppKey { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [Required]
        [StringLength(50)]
        [Display(Name = "App type")]
        public string AppType { get; set; } = string.Empty;

        [StringLength(500)]
        [Display(Name = "Description")]
        public string? Description { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;

        [Display(Name = "Sort order")]
        public int SortOrder { get; set; }
    }
}
