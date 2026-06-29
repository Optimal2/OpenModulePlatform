// File: OpenModulePlatform.Portal/Pages/Admin/InstanceTemplateAppEdit.cshtml.cs
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
/// Edits the desired app row on the current installation profile.
/// HostAgent materializes these values into concrete app instances; app instance pages show the result.
/// </summary>
public sealed class InstanceTemplateAppEditModel : OmpPortalPageModel
{
    private const byte DesiredStateShouldRun = 1;

    private static readonly Regex KeyPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$",
        RegexOptions.Compiled);

    private readonly OmpAdminRepository _repo;

    public InstanceTemplateAppEditModel(
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

    public IReadOnlyList<OptionItem> TemplateModuleOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> TemplateHostOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> HostRoleOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> AppOptions { get; private set; } = [];

    public IReadOnlyList<ArtifactSelectionOption> ArtifactOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> DesiredStateOptions =>
    [
        Opt("0", T("Unknown / unmanaged")),
        Opt("1", T("Enabled / should run")),
        Opt("2", T("Stopped / should not run"))
    ];

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsCreate => Input.InstanceTemplateAppInstanceId == 0;

    public async Task<IActionResult> OnGet(int? id, int? templateId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (id.HasValue)
        {
            var row = await _repo.GetInstanceTemplateAppInstanceAsync(id.Value, ct);
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
            Input.IsAllowed = true;
            Input.DesiredState = 1;
        }

        await LoadAsync(ct);
        SetTitles(IsCreate ? "Add desired app" : "Edit desired app");
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
        SetTitles(IsCreate ? "Add desired app" : "Edit desired app");

        await ValidateInputAsync(ct);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var id = await _repo.SaveInstanceTemplateAppInstanceAsync(
                new InstanceTemplateAppInstanceEditData
                {
                    InstanceTemplateAppInstanceId = Input.InstanceTemplateAppInstanceId,
                    InstanceTemplateId = Input.InstanceTemplateId,
                    InstanceTemplateModuleInstanceId = Input.InstanceTemplateModuleInstanceId,
                    InstanceTemplateHostId = Input.InstanceTemplateHostId,
                    TargetHostTemplateId = Input.TargetHostTemplateId,
                    AppId = Input.AppId,
                    AppInstanceKey = Input.AppInstanceKey.Trim(),
                    DisplayName = Input.DisplayName.Trim(),
                    Description = Clean(Input.Description),
                    RoutePath = Clean(Input.RoutePath),
                    PublicUrl = Clean(Input.PublicUrl),
                    InstallPath = Clean(Input.InstallPath),
                    InstallationName = Clean(Input.InstallationName),
                    DesiredArtifactId = Input.DesiredArtifactId,
                    DesiredConfigId = Input.DesiredConfigId,
                    ExpectedLogin = Clean(Input.ExpectedLogin),
                    ExpectedClientHostName = Clean(Input.ExpectedClientHostName),
                    ExpectedClientIp = Clean(Input.ExpectedClientIp),
                    IsEnabled = Input.IsEnabled,
                    IsAllowed = Input.IsAllowed,
                    DesiredState = Input.DesiredState,
                    SortOrder = Input.SortOrder
                },
                ct);

            StatusMessage = IsCreate ? T("Desired app added.") : T("Desired app updated.");
            return RedirectToPage("/Admin/InstanceTemplateAppEdit", new { id });
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
            await _repo.DeleteInstanceTemplateAppInstanceAsync(Input.InstanceTemplateAppInstanceId, ct);
            StatusMessage = T("Desired app removed.");
            return RedirectToPage("/Admin/InstanceTemplateEdit", new { id = templateId });
        }
        catch (SqlException ex)
        {
            await LoadAsync(ct);
            SetTitles("Edit desired app");
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
            return;
        }

        TemplateModuleOptions = await _repo.GetInstanceTemplateModuleOptionsAsync(Input.InstanceTemplateId, ct);
        TemplateHostOptions = await _repo.GetInstanceTemplateHostOptionsAsync(Input.InstanceTemplateId, ct);
        HostRoleOptions = await _repo.GetHostTemplateOptionsAsync(ct);
        AppOptions = await _repo.GetAppOptionsAsync(ct);
        ArtifactOptions = await _repo.GetArtifactOptionsAsync(ct);
    }

    private async Task ValidateInputAsync(CancellationToken ct)
    {
        ValidateBasicInput();
        if (!ModelState.IsValid)
        {
            return;
        }

        var templateModule = await _repo.GetInstanceTemplateModuleContextAsync(
            Input.InstanceTemplateModuleInstanceId,
            ct);

        if (templateModule is null || templateModule.InstanceTemplateId != Input.InstanceTemplateId)
        {
            ModelState.AddModelError(
                nameof(Input.InstanceTemplateModuleInstanceId),
                T("Selected module instance was not found in this installation."));
            return;
        }

        if (Input.InstanceTemplateHostId.HasValue)
        {
            var templateHost = await _repo.GetInstanceTemplateHostContextAsync(
                Input.InstanceTemplateHostId.Value,
                ct);

            if (templateHost is null || templateHost.InstanceTemplateId != Input.InstanceTemplateId)
            {
                ModelState.AddModelError(
                    nameof(Input.InstanceTemplateHostId),
                    T("Selected host was not found in this installation."));
            }
        }

        if (Input.InstanceTemplateHostId.HasValue && Input.TargetHostTemplateId.HasValue)
        {
            ModelState.AddModelError(
                nameof(Input.TargetHostTemplateId),
                T("Choose either one specific host or one host role, not both."));
        }

        if (Input.TargetHostTemplateId is int targetHostTemplateId
            && !HostRoleOptions.Any(option => string.Equals(option.Value, targetHostTemplateId.ToString(), StringComparison.Ordinal)))
        {
            ModelState.AddModelError(
                nameof(Input.TargetHostTemplateId),
                T("Selected host role was not found."));
        }

        var app = await _repo.GetAppContextAsync(Input.AppId, ct);
        if (app is null)
        {
            ModelState.AddModelError(nameof(Input.AppId), T("Selected app was not found."));
            return;
        }

        if (app.ModuleId != templateModule.ModuleId)
        {
            ModelState.AddModelError(
                nameof(Input.AppId),
                T("The selected app does not belong to the selected module instance's module."));
        }

        var isActiveDesired = Input.IsEnabled
            && Input.IsAllowed
            && Input.DesiredState == DesiredStateShouldRun;

        if (isActiveDesired
            && !Input.InstanceTemplateHostId.HasValue
            && !Input.TargetHostTemplateId.HasValue
            && !AllowsHostNeutralPlacement(app))
        {
            ModelState.AddModelError(
                nameof(Input.InstanceTemplateHostId),
                T("Choose a host or host role for runtime apps. Leave both empty only for host-neutral web apps behind a load balancer."));
        }

        if (Input.DesiredArtifactId.HasValue)
        {
            var belongsToApp = await _repo.ArtifactBelongsToAppAsync(
                Input.DesiredArtifactId.Value,
                Input.AppId,
                ct);

            if (!belongsToApp)
            {
                ModelState.AddModelError(
                    nameof(Input.DesiredArtifactId),
                    T("The selected artifact does not belong to the selected app."));
            }
            else if (!await _repo.ArtifactCanBindToAppAsync(Input.DesiredArtifactId.Value, Input.AppId, ct))
            {
                ModelState.AddModelError(
                    nameof(Input.DesiredArtifactId),
                    T("The selected artifact package type is not compatible with the selected app type."));
            }
        }

        if (isActiveDesired
            && await _repo.ActiveTemplateAppPlacementConflictExistsAsync(
                Input.InstanceTemplateAppInstanceId,
                Input.InstanceTemplateModuleInstanceId,
                Input.InstanceTemplateHostId,
                Input.TargetHostTemplateId,
                Input.AppId,
                ct))
        {
            ModelState.AddModelError(
                nameof(Input.InstanceTemplateHostId),
                T("Only one active desired app row can exist for the selected app and host placement."));
        }
    }

    private void ValidateBasicInput()
    {
        if (Input.InstanceTemplateId <= 0)
        {
            ModelState.AddModelError(nameof(Input.InstanceTemplateId), T("Select an installation."));
        }

        if (Input.InstanceTemplateModuleInstanceId <= 0)
        {
            ModelState.AddModelError(
                nameof(Input.InstanceTemplateModuleInstanceId),
                T("Select a module instance."));
        }

        if (Input.AppId <= 0)
        {
            ModelState.AddModelError(nameof(Input.AppId), T("Select an app definition."));
        }

        if (!KeyPattern.IsMatch(Input.AppInstanceKey ?? string.Empty))
        {
            ModelState.AddModelError(
                nameof(Input.AppInstanceKey),
                T("Use a stable key with letters, digits, dash, underscore or dot."));
        }

        if (!string.IsNullOrWhiteSpace(Input.PublicUrl)
            && !Uri.TryCreate(Input.PublicUrl, UriKind.Absolute, out _))
        {
            ModelState.AddModelError(
                nameof(Input.PublicUrl),
                T("Public URL must be an absolute URL."));
        }
    }

    private static InputModel ToInput(InstanceTemplateAppInstanceEditData row)
        => new()
        {
            InstanceTemplateAppInstanceId = row.InstanceTemplateAppInstanceId,
            InstanceTemplateId = row.InstanceTemplateId,
            InstanceTemplateModuleInstanceId = row.InstanceTemplateModuleInstanceId,
            InstanceTemplateHostId = row.InstanceTemplateHostId,
            TargetHostTemplateId = row.TargetHostTemplateId,
            AppId = row.AppId,
            AppInstanceKey = row.AppInstanceKey,
            DisplayName = row.DisplayName,
            Description = row.Description,
            RoutePath = row.RoutePath,
            PublicUrl = row.PublicUrl,
            InstallPath = row.InstallPath,
            InstallationName = row.InstallationName,
            DesiredArtifactId = row.DesiredArtifactId,
            DesiredConfigId = row.DesiredConfigId,
            ExpectedLogin = row.ExpectedLogin,
            ExpectedClientHostName = row.ExpectedClientHostName,
            ExpectedClientIp = row.ExpectedClientIp,
            IsEnabled = row.IsEnabled,
            IsAllowed = row.IsAllowed,
            DesiredState = row.DesiredState,
            SortOrder = row.SortOrder
        };

    private static OptionItem Opt(string value, string label)
        => new() { Value = value, Label = label };

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool AllowsHostNeutralPlacement(AppDefinitionContext app)
        => app.AllowMultipleActiveInstances
           || string.Equals(app.AppType, "WebApp", StringComparison.OrdinalIgnoreCase)
           || string.Equals(app.AppType, "Portal", StringComparison.OrdinalIgnoreCase);

    private static string ToFriendlySqlMessage(SqlException ex)
        => ex.Number switch
        {
            2601 or 2627 => "A desired app with the same key or active host placement already exists.",
            >= 51050 and <= 51061 => "Only one active desired app row can exist for the selected app and host placement.",
            51063 => ex.Message,
            547 => "Update dependent references first.",
            _ => "The desired app could not be saved."
        };

    public sealed class InputModel
    {
        public int InstanceTemplateAppInstanceId { get; set; }

        public int InstanceTemplateId { get; set; }

        [Required]
        [Display(Name = "Module instance")]
        public int InstanceTemplateModuleInstanceId { get; set; }

        [Display(Name = "Host")]
        public int? InstanceTemplateHostId { get; set; }

        [Display(Name = "Host role")]
        public int? TargetHostTemplateId { get; set; }

        [Required]
        [Display(Name = "App definition")]
        public int AppId { get; set; }

        [Required]
        [StringLength(100)]
        [Display(Name = "App instance key")]
        public string AppInstanceKey { get; set; } = string.Empty;

        [Required]
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [StringLength(500)]
        public string? Description { get; set; }

        [StringLength(256)]
        [Display(Name = "Route path")]
        public string? RoutePath { get; set; }

        [StringLength(500)]
        [Display(Name = "Public URL")]
        public string? PublicUrl { get; set; }

        [StringLength(500)]
        [Display(Name = "Install path")]
        public string? InstallPath { get; set; }

        [StringLength(150)]
        [Display(Name = "Installation name")]
        public string? InstallationName { get; set; }

        [Display(Name = "Desired artifact")]
        public int? DesiredArtifactId { get; set; }

        [Display(Name = "ConfigId")]
        public int? DesiredConfigId { get; set; }

        [StringLength(256)]
        [Display(Name = "Expected login")]
        public string? ExpectedLogin { get; set; }

        [StringLength(128)]
        [Display(Name = "Expected client host")]
        public string? ExpectedClientHostName { get; set; }

        [StringLength(64)]
        [Display(Name = "Expected client IP")]
        public string? ExpectedClientIp { get; set; }

        [Display(Name = "Enabled")]
        public bool IsEnabled { get; set; } = true;

        [Display(Name = "Allowed")]
        public bool IsAllowed { get; set; } = true;

        [Display(Name = "Desired state")]
        public byte DesiredState { get; set; } = 1;

        [Display(Name = "Sort order")]
        public int SortOrder { get; set; }
    }
}
