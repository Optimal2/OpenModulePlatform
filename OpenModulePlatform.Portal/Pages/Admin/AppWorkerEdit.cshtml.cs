// File: OpenModulePlatform.Portal/Pages/Admin/AppWorkerEdit.cshtml.cs
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

public sealed class AppWorkerEditModel : OmpPortalPageModel
{
    private static readonly Regex KeyPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$",
        RegexOptions.Compiled);

    private readonly OmpAdminRepository _repo;

    public AppWorkerEditModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<OptionItem> AppOptions { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    [BindProperty]
    public bool HasExistingDefinition { get; set; }

    public bool IsCreate => !HasExistingDefinition;

    public string SelectedAppLabel
        => AppOptions.FirstOrDefault(x => x.Value == Input.AppId.ToString())?.Label ?? Input.AppId.ToString();

    public async Task<IActionResult> OnGet(int? id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles(id.HasValue ? "Edit app worker definition" : "Create app worker definition");

        if (!id.HasValue)
        {
            HasExistingDefinition = false;
            Input.RuntimeKind = "windows-worker-plugin";
            Input.IsEnabled = true;
            return Page();
        }

        var row = await _repo.GetAppWorkerDefinitionAsync(id.Value, ct);
        if (row is null)
        {
            HasExistingDefinition = false;
            Input.AppId = id.Value;
            Input.RuntimeKind = "windows-worker-plugin";
            Input.IsEnabled = true;
            return Page();
        }

        HasExistingDefinition = true;
        Input = new InputModel
        {
            AppId = row.AppId,
            RuntimeKind = row.RuntimeKind,
            WorkerTypeKey = row.WorkerTypeKey,
            PluginRelativePath = row.PluginRelativePath,
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
        SetTitles(HasExistingDefinition ? "Edit app worker definition" : "Create app worker definition");

        await ValidateInputAsync(ct);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var id = await _repo.SaveAppWorkerDefinitionAsync(
                new AppWorkerDefinitionEditData
                {
                    AppId = Input.AppId,
                    RuntimeKind = Input.RuntimeKind.Trim(),
                    WorkerTypeKey = Input.WorkerTypeKey.Trim(),
                    PluginRelativePath = NormalizePath(Input.PluginRelativePath),
                    IsEnabled = Input.IsEnabled
                },
                ct);

            StatusMessage = T("App worker definition saved.");
            return RedirectToPage("/Admin/AppWorkerEdit", new { id });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
            return Page();
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The app worker definition could not be saved.")));

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
            await _repo.DeleteAppWorkerDefinitionAsync(Input.AppId, ct);
            StatusMessage = T("App worker definition deleted.");
            return RedirectToPage("/Admin/AppWorkers");
        }
        catch (InvalidOperationException ex)
        {
            await LoadAsync(ct);
            SetTitles("Edit app worker definition");
            ModelState.AddModelError(string.Empty, T(ex.Message));
            return Page();
        }
        catch (SqlException ex)
        {
            await LoadAsync(ct);
            SetTitles("Edit app worker definition");
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The app worker definition could not be deleted.")));
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        AppOptions = await _repo.GetAppOptionsAsync(ct);
    }

    private async Task ValidateInputAsync(CancellationToken ct)
    {
        if (Input.AppId <= 0)
        {
            ModelState.AddModelError(nameof(Input.AppId), T("Select an app definition."));
        }

        if (string.IsNullOrWhiteSpace(Input.RuntimeKind))
        {
            ModelState.AddModelError(nameof(Input.RuntimeKind), T("Enter a runtime kind."));
        }

        if (!KeyPattern.IsMatch(Input.WorkerTypeKey ?? string.Empty))
        {
            ModelState.AddModelError(
                nameof(Input.WorkerTypeKey),
                T("Use a stable worker type key with letters, digits, dash, underscore or dot."));
        }

        if (!IsSafeRelativePath(Input.PluginRelativePath))
        {
            ModelState.AddModelError(
                nameof(Input.PluginRelativePath),
                T("Plugin path must be a relative path under the install root and must not contain parent traversal."));
        }

        if (!ModelState.IsValid)
        {
            return;
        }

        var app = await _repo.GetAppContextAsync(Input.AppId, ct);
        if (app is null)
        {
            ModelState.AddModelError(nameof(Input.AppId), T("Selected app was not found."));
        }
    }

    private static string NormalizePath(string value)
        => value.Trim().Replace('\\', '/');

    private static bool IsSafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = NormalizePath(value);
        if (Path.IsPathRooted(normalized))
        {
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        return !segments.Any(x => x == "." || x == "..");
    }

    private static string ToFriendlySqlMessage(SqlException ex, string fallback)
        => ex.Number switch
        {
            2601 or 2627 => "A worker definition already exists for the selected app.",
            547 => "Select a valid existing app definition first.",
            _ => fallback
        };

    public sealed class InputModel
    {
        [Required]
        [Display(Name = "App definition")]
        public int AppId { get; set; }

        [Required]
        [StringLength(100)]
        [Display(Name = "Runtime kind")]
        public string RuntimeKind { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        [Display(Name = "Worker type key")]
        public string WorkerTypeKey { get; set; } = string.Empty;

        [Required]
        [StringLength(400)]
        [Display(Name = "Plugin relative path")]
        public string PluginRelativePath { get; set; } = string.Empty;

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;
    }
}
