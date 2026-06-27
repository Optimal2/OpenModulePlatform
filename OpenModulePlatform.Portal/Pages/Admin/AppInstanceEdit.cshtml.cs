// File: OpenModulePlatform.Portal/Pages/Admin/AppInstanceEdit.cshtml.cs
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
/// Edits an app instance.
/// This is the main manual runtime configuration surface for host placement, routing, artifact selection and verification policy.
/// </summary>
public sealed class AppInstanceEditModel : OmpPortalPageModel
{
    private const byte DesiredStateShouldRun = 1;

    private static readonly Regex KeyPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$",
        RegexOptions.Compiled);

    private readonly OmpAdminRepository _repo;

    public AppInstanceEditModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<OptionItem> ModuleInstanceOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> HostOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> AppOptions { get; private set; } = [];

    public IReadOnlyList<ArtifactSelectionOption> ArtifactOptions { get; private set; } = [];

    public TemplateManagedAppInstanceInfo? TemplateControl { get; private set; }

    public IReadOnlyList<OptionItem> DesiredStateOptions =>
    [
        Opt("0", T("Unknown / unmanaged")),
        Opt("1", T("Enabled / should run")),
        Opt("2", T("Stopped / should not run"))
    ];

    [TempData]
    public string? StatusMessage { get; set; }

    public bool IsCreate => Input.AppInstanceId == Guid.Empty;

    public async Task<IActionResult> OnGet(Guid? id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(ct);
        SetTitles(id.HasValue ? "Edit app instance" : "Create app instance");

        if (!id.HasValue)
        {
            Input.IsEnabled = true;
            Input.IsAllowed = true;
            Input.DesiredState = 1;
            return Page();
        }

        var row = await _repo.GetAppInstanceAsync(id.Value, ct);
        if (row is null)
        {
            return NotFound();
        }

        Input = new InputModel
        {
            AppInstanceId = row.AppInstanceId,
            ModuleInstanceId = row.ModuleInstanceId,
            HostId = row.HostId,
            AppId = row.AppId,
            AppInstanceKey = row.AppInstanceKey,
            DisplayName = row.DisplayName,
            Description = row.Description,
            RoutePath = row.RoutePath,
            PublicUrl = row.PublicUrl,
            InstallPath = row.InstallPath,
            InstallationName = row.InstallationName,
            ArtifactId = row.ArtifactId,
            ConfigId = row.ConfigId,
            ExpectedLogin = row.ExpectedLogin,
            ExpectedClientHostName = row.ExpectedClientHostName,
            ExpectedClientIp = row.ExpectedClientIp,
            IsEnabled = row.IsEnabled,
            IsAllowed = row.IsAllowed,
            DesiredState = row.DesiredState,
            SortOrder = row.SortOrder
        };
        TemplateControl = await _repo.GetTemplateManagedAppInstanceInfoAsync(id.Value, ct);

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
        SetTitles(IsCreate ? "Create app instance" : "Edit app instance");
        if (!IsCreate)
        {
            TemplateControl = await _repo.GetTemplateManagedAppInstanceInfoAsync(Input.AppInstanceId, ct);
            if (TemplateControl is not null)
            {
                ModelState.AddModelError(
                    string.Empty,
                    T("This app instance is managed by the installation topology. Change the desired app from System > Installation and let HostAgent update the runtime row."));
                return Page();
            }
        }

        await ValidateInputAsync(ct);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var id = await _repo.SaveAppInstanceAsync(
                new AppInstanceEditData
                {
                    AppInstanceId = Input.AppInstanceId,
                    ModuleInstanceId = Input.ModuleInstanceId,
                    HostId = Input.HostId,
                    AppId = Input.AppId,
                    AppInstanceKey = Input.AppInstanceKey.Trim(),
                    DisplayName = Input.DisplayName.Trim(),
                    Description = Clean(Input.Description),
                    RoutePath = CleanRoutePath(Input.RoutePath),
                    PublicUrl = Clean(Input.PublicUrl),
                    InstallPath = Clean(Input.InstallPath),
                    InstallationName = Clean(Input.InstallationName),
                    ArtifactId = Input.ArtifactId,
                    ConfigId = Input.ConfigId,
                    ExpectedLogin = Clean(Input.ExpectedLogin),
                    ExpectedClientHostName = Clean(Input.ExpectedClientHostName),
                    ExpectedClientIp = Clean(Input.ExpectedClientIp),
                    IsEnabled = Input.IsEnabled,
                    IsAllowed = Input.IsAllowed,
                    DesiredState = Input.DesiredState,
                    SortOrder = Input.SortOrder
                },
                ct);

            StatusMessage = IsCreate ? T("App instance created.") : T("App instance updated.");
            return RedirectToPage("/Admin/AppInstanceEdit", new { id });
        }
        catch (SqlException ex)
        {
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The app instance could not be saved.")));

            return Page();
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, T(ex.Message));
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
            await _repo.DeleteAppInstanceAsync(Input.AppInstanceId, ct);
            StatusMessage = T("App instance deleted.");
            return RedirectToPage("/Admin/AppInstances");
        }
        catch (SqlException ex)
        {
            await LoadAsync(ct);
            SetTitles("Edit app instance");
            ModelState.AddModelError(
                string.Empty,
                T(ToFriendlySqlMessage(ex, "The app instance could not be deleted.")));

            return Page();
        }
        catch (InvalidOperationException ex)
        {
            await LoadAsync(ct);
            SetTitles("Edit app instance");
            if (Input.AppInstanceId != Guid.Empty)
            {
                TemplateControl = await _repo.GetTemplateManagedAppInstanceInfoAsync(Input.AppInstanceId, ct);
            }

            ModelState.AddModelError(string.Empty, T(ex.Message));
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        ModuleInstanceOptions = await _repo.GetModuleInstanceOptionsAsync(ct);
        HostOptions = await _repo.GetHostOptionsAsync(ct);
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

        await ValidateCrossEntitySelectionsAsync(ct);
    }

    private void ValidateBasicInput()
    {
        if (Input.ModuleInstanceId == Guid.Empty)
        {
            ModelState.AddModelError(nameof(Input.ModuleInstanceId), T("Select a module instance."));
        }

        if (Input.AppId <= 0)
        {
            ModelState.AddModelError(nameof(Input.AppId), T("Select an app definition."));
        }

        if (!KeyPattern.IsMatch(Input.AppInstanceKey ?? string.Empty))
        {
            ModelState.AddModelError(
                nameof(Input.AppInstanceKey), T("Use a stable key with letters, digits, dash, underscore or dot."));
        }

        if (!string.IsNullOrWhiteSpace(Input.PublicUrl)
            && !Uri.TryCreate(Input.PublicUrl, UriKind.Absolute, out _))
        {
            ModelState.AddModelError(
                nameof(Input.PublicUrl), T("Public URL must be an absolute URL."));
        }
    }

    private async Task ValidateCrossEntitySelectionsAsync(CancellationToken ct)
    {
        var moduleInstance = await _repo.GetModuleInstanceContextAsync(Input.ModuleInstanceId, ct);
        if (moduleInstance is null)
        {
            ModelState.AddModelError(
                nameof(Input.ModuleInstanceId), T("Selected module instance was not found."));

            return;
        }

        var app = await _repo.GetAppContextAsync(Input.AppId, ct);
        if (app is null)
        {
            ModelState.AddModelError(nameof(Input.AppId), T("Selected app was not found."));
            return;
        }

        if (app.ModuleId != moduleInstance.ModuleId)
        {
            ModelState.AddModelError(
                nameof(Input.AppId), T("The selected app does not belong to the selected module instance's module."));
        }

        var isActiveDesired = Input.IsEnabled
            && Input.IsAllowed
            && Input.DesiredState == DesiredStateShouldRun;

        if (isActiveDesired
            && !Input.HostId.HasValue
            && !AllowsHostNeutralPlacement(app))
        {
            ModelState.AddModelError(
                nameof(Input.HostId),
                T("Choose a host for runtime apps. Leave host empty only for host-neutral web apps behind a load balancer."));
        }

        if (Input.HostId.HasValue)
        {
            await ValidateHostSelectionAsync(moduleInstance, Input.HostId.Value, ct);
        }

        if (Input.ArtifactId.HasValue)
        {
            await ValidateArtifactSelectionAsync(Input.ArtifactId.Value, Input.AppId, ct);
        }

        if (isActiveDesired
            && await _repo.ActiveAppInstancePlacementConflictExistsAsync(
                Input.AppInstanceId,
                Input.ModuleInstanceId,
                Input.HostId,
                Input.AppId,
                ct))
        {
            ModelState.AddModelError(
                nameof(Input.HostId),
                T("Only one active desired app row can exist for the selected app and host placement."));
        }
    }

    private async Task ValidateHostSelectionAsync(
        ModuleInstanceContext moduleInstance,
        Guid hostId,
        CancellationToken ct)
    {
        var host = await _repo.GetHostContextAsync(hostId, ct);
        if (host is null)
        {
            ModelState.AddModelError(nameof(Input.HostId), T("Selected host was not found."));
            return;
        }

        if (host.InstanceId != moduleInstance.InstanceId)
        {
            ModelState.AddModelError(
                nameof(Input.HostId), T("The selected host belongs to a different instance than the selected module instance."));
        }
    }

    private async Task ValidateArtifactSelectionAsync(int artifactId, int appId, CancellationToken ct)
    {
        var belongsToApp = await _repo.ArtifactBelongsToAppAsync(artifactId, appId, ct);
        if (!belongsToApp)
        {
            ModelState.AddModelError(
                nameof(Input.ArtifactId), T("The selected artifact does not belong to the selected app."));
        }
    }

    private static OptionItem Opt(string value, string label)
        => new() { Value = value, Label = label };

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? CleanRoutePath(string? value)
    {
        return Clean(value);
    }

    private static bool AllowsHostNeutralPlacement(AppDefinitionContext app)
        => app.AllowMultipleActiveInstances
           || string.Equals(app.AppType, "WebApp", StringComparison.OrdinalIgnoreCase)
           || string.Equals(app.AppType, "Portal", StringComparison.OrdinalIgnoreCase);

    private static string ToFriendlySqlMessage(SqlException ex, string fallback)
        => ex.Number switch
        {
            2601 or 2627 => "An app instance with the same key or active host placement already exists.",
            >= 51050 and <= 51061 => "Only one active desired app row can exist for the selected app and host placement.",
            51063 => ex.Message,
            547 => "Update dependent references first.",
            _ => fallback
        };

    public sealed class InputModel
    {
        public Guid AppInstanceId { get; set; }

        [Required]
        [Display(Name = "Module instance")]
        public Guid ModuleInstanceId { get; set; }

        [Display(Name = "Host")]
        public Guid? HostId { get; set; }

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

        [Display(Name = "Artifact")]
        public int? ArtifactId { get; set; }

        [Display(Name = "ConfigId")]
        public int? ConfigId { get; set; }

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
