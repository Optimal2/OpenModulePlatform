// File: OpenModulePlatform.Portal/Pages/Admin/HostEdit.cshtml.cs
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
/// Edits a host row within an OMP instance.
/// Hosts are the manual placement target until template materialization and HostAgent automation are completed.
/// </summary>
public sealed class HostEditModel : OmpPortalPageModel
{
    private static readonly Regex KeyPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.Compiled);

    private readonly OmpAdminRepository _repo;

    public HostEditModel(
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

    public IReadOnlyList<OptionItem> EnvironmentOptions { get; } =
    [
        Opt("dev", "Development"),
        Opt("test", "Test"),
        Opt("stage", "Stage"),
        Opt("prod", "Production")
    ];

    public IReadOnlyList<OptionItem> OsFamilyOptions { get; } =
    [
        Opt("Windows", "Windows"),
        Opt("Linux", "Linux"),
        Opt("macOS", "macOS")
    ];

    public IReadOnlyList<OptionItem> ArchitectureOptions { get; } =
    [
        Opt("x64", "x64"),
        Opt("arm64", "arm64"),
        Opt("x86", "x86")
    ];

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsCreate => Input.HostId == Guid.Empty;

    public async Task<IActionResult> OnGet(Guid? id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles(id.HasValue ? "Edit host" : "Create host");

        if (!id.HasValue)
        {
            return Page();
        }

        var row = await _repo.GetHostAsync(id.Value, ct);
        if (row is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            HostId = row.HostId,
            InstanceId = row.InstanceId,
            HostKey = row.HostKey,
            DisplayName = row.DisplayName,
            Environment = row.Environment,
            OsFamily = row.OsFamily,
            OsVersion = row.OsVersion,
            Architecture = row.Architecture,
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
        SetTitles(IsCreate ? "Create host" : "Edit host");

        ValidateInput();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var id = await _repo.SaveHostAsync(
                new HostEditData
                {
                    HostId = Input.HostId,
                    InstanceId = Input.InstanceId,
                    HostKey = Input.HostKey.Trim(),
                    DisplayName = Clean(Input.DisplayName),
                    Environment = Clean(Input.Environment),
                    OsFamily = Clean(Input.OsFamily),
                    OsVersion = Clean(Input.OsVersion),
                    Architecture = Clean(Input.Architecture),
                    IsEnabled = Input.IsEnabled
                },
                ct);

            StatusMessage = IsCreate ? "Host created." : "Host updated.";
            return RedirectToPage("/Admin/HostEdit", new { id });
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(
                string.Empty,
                ToFriendlySqlMessage(ex, "The host could not be saved."));

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
            await _repo.DeleteHostAsync(Input.HostId, ct);
            StatusMessage = "Host deleted.";
            return RedirectToPage("/Admin/Hosts");
        }
        catch (SqlException ex)
        {
            await LoadAsync(ct);
            SetTitles("Edit host");
            ModelState.AddModelError(
                string.Empty,
                ToFriendlySqlMessage(ex, "The host could not be deleted."));

            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        InstanceOptions = await _repo.GetInstanceOptionsAsync(ct);
    }

    private void ValidateInput()
    {
        if (Input.InstanceId == Guid.Empty)
        {
            ModelState.AddModelError(nameof(Input.InstanceId), "Select an instance.");
        }

        if (!KeyPattern.IsMatch(Input.HostKey ?? string.Empty))
        {
            ModelState.AddModelError(
                nameof(Input.HostKey),
                "Use a stable host key with letters, digits, dash, underscore or dot.");
        }
    }

    private static OptionItem Opt(string value, string label)
        => new() { Value = value, Label = label };

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToFriendlySqlMessage(SqlException ex, string fallback)
        => ex.Number switch
        {
            2601 or 2627 => "A host with the same key already exists in the selected instance.",
            547 => "Delete or update dependent rows first.",
            _ => fallback
        };

    public sealed class InputModel
    {
        public Guid HostId { get; set; }

        [Required]
        [Display(Name = "Instance")]
        public Guid InstanceId { get; set; }

        [Required]
        [StringLength(128)]
        [Display(Name = "Host key")]
        public string HostKey { get; set; } = string.Empty;

        [StringLength(200)]
        [Display(Name = "Display name")]
        public string? DisplayName { get; set; }

        [StringLength(100)]
        public string? Environment { get; set; }

        [StringLength(50)]
        [Display(Name = "OS family")]
        public string? OsFamily { get; set; }

        [StringLength(100)]
        [Display(Name = "OS version")]
        public string? OsVersion { get; set; }

        [StringLength(50)]
        public string? Architecture { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;
    }
}
