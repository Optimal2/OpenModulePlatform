// File: OpenModulePlatform.Portal/Pages/Admin/InstanceTemplateHostEdit.cshtml.cs
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
/// Edits a desired host row on the current installation profile.
/// </summary>
public sealed class InstanceTemplateHostEditModel : OmpPortalPageModel
{
    private static readonly Regex KeyPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.Compiled);

    private readonly OmpAdminRepository _repo;

    public InstanceTemplateHostEditModel(
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

    public IReadOnlyList<OptionItem> HostTemplateOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> EnvironmentOptions =>
    [
        Opt("dev", T("Development")),
        Opt("test", T("Test")),
        Opt("stage", T("Stage")),
        Opt("prod", T("Production"))
    ];

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsCreate => Input.InstanceTemplateHostId == 0;

    public async Task<IActionResult> OnGet(int? id, int? templateId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (id.HasValue)
        {
            var row = await _repo.GetInstanceTemplateHostAsync(id.Value, ct);
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
        SetTitles(IsCreate ? "Add host" : "Edit host");
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
        SetTitles(IsCreate ? "Add host" : "Edit host");

        ValidateInput();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var id = await _repo.SaveInstanceTemplateHostAsync(
                new InstanceTemplateHostEditData
                {
                    InstanceTemplateHostId = Input.InstanceTemplateHostId,
                    InstanceTemplateId = Input.InstanceTemplateId,
                    HostTemplateId = Input.HostTemplateId,
                    HostKey = Input.HostKey.Trim(),
                    DisplayName = Input.DisplayName.Trim(),
                    Environment = Clean(Input.Environment),
                    SortOrder = Input.SortOrder,
                    IsEnabled = Input.IsEnabled
                },
                ct);

            StatusMessage = IsCreate ? T("Host added.") : T("Host updated.");
            return RedirectToPage("/Admin/InstanceTemplateHostEdit", new { id });
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

        ModelState.Clear();
        var templateId = Input.InstanceTemplateId;
        var hostId = Input.InstanceTemplateHostId;

        try
        {
            await _repo.DeleteInstanceTemplateHostAsync(hostId, ct);
            StatusMessage = T("Host removed.");
            return RedirectToPage("/Admin/InstanceTemplateEdit", new { id = templateId });
        }
        catch (SqlException ex)
        {
            var row = await _repo.GetInstanceTemplateHostAsync(hostId, ct);
            if (row is not null)
            {
                Input = ToInput(row);
            }

            await LoadAsync(ct);
            SetTitles("Edit host");
            ModelState.AddModelError(string.Empty, T(ToFriendlySqlMessage(ex)));
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Template = await _repo.GetInstanceTemplateAsync(Input.InstanceTemplateId, ct);
        if (Template is null)
        {
            ModelState.AddModelError(string.Empty, T("Installation topology was not found."));
        }

        HostTemplateOptions = await _repo.GetHostTemplateOptionsAsync(ct);
    }

    private void ValidateInput()
    {
        if (Input.InstanceTemplateId <= 0)
        {
            ModelState.AddModelError(nameof(Input.InstanceTemplateId), T("Select an installation."));
        }

        if (Input.HostTemplateId <= 0)
        {
            ModelState.AddModelError(nameof(Input.HostTemplateId), T("Select a host role."));
        }

        if (!KeyPattern.IsMatch(Input.HostKey ?? string.Empty))
        {
            ModelState.AddModelError(
                nameof(Input.HostKey),
                T("Use a stable host key with letters, digits, dash, underscore or dot."));
        }
    }

    private static InputModel ToInput(InstanceTemplateHostEditData row)
        => new()
        {
            InstanceTemplateHostId = row.InstanceTemplateHostId,
            InstanceTemplateId = row.InstanceTemplateId,
            HostTemplateId = row.HostTemplateId,
            HostKey = row.HostKey,
            DisplayName = row.DisplayName,
            Environment = row.Environment,
            SortOrder = row.SortOrder,
            IsEnabled = row.IsEnabled
        };

    private static OptionItem Opt(string value, string label)
        => new() { Value = value, Label = label };

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToFriendlySqlMessage(SqlException ex)
        => ex.Number switch
        {
            2601 or 2627 => "A host with the same key already exists in this installation.",
            547 => "Move or delete desired app rows that use this host first.",
            _ => "The host could not be saved."
        };

    public sealed class InputModel
    {
        public int InstanceTemplateHostId { get; set; }

        public int InstanceTemplateId { get; set; }

        [Required]
        [Display(Name = "Host role")]
        public int HostTemplateId { get; set; }

        [Required]
        [StringLength(128)]
        [Display(Name = "Host key")]
        public string HostKey { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(100)]
        public string? Environment { get; set; }

        [Display(Name = "Sort order")]
        public int SortOrder { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;
    }
}
