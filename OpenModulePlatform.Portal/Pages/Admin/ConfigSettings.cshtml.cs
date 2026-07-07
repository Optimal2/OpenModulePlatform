// File: OpenModulePlatform.Portal/Pages/Admin/ConfigSettings.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class ConfigSettingsModel : OmpPortalPageModel
{
    private const string ScopeGlobal = "Global";
    private const string ScopeUser = "User";
    private const string ScopePermission = "Permission";
    private const string ScopeRole = "Role";

    private readonly OmpConfigSettingsAdminRepository _repo;
    private readonly OmpConfigurationService _configuration;

    public ConfigSettingsModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpConfigSettingsAdminRepository repo,
        OmpConfigurationService configuration)
        : base(options, rbac)
    {
        _repo = repo;
        _configuration = configuration;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public IReadOnlyList<ConfigSettingDefinitionRow> Definitions { get; private set; } = [];

    public IReadOnlyList<ConfigSettingValueRow> Rows { get; private set; } = [];

    public IReadOnlyList<OptionItem> SettingOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> UserOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> PermissionOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> RoleOptions { get; private set; } = [];

    public bool IsEdit => Input.ConfigId > 0;

    public IReadOnlyList<OptionItem> ScopeOptions =>
    [
        Opt(ScopeGlobal, T("Global")),
        Opt(ScopeUser, T("User")),
        Opt(ScopePermission, T("Permission")),
        Opt(ScopeRole, T("Role"))
    ];

    public async Task<IActionResult> OnGet(int? configId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Config settings");
        await LoadPageDataAsync(ct);

        if (configId.HasValue)
        {
            var row = await _repo.GetValueAsync(configId.Value, ct);
            if (row is null)
            {
                return NotFound();
            }

            Input = ToInput(row);
        }
        else
        {
            Input.ScopeType = ScopeGlobal;
        }

        return Page();
    }

    public async Task<IActionResult> OnPost(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Config settings");
        await LoadPageDataAsync(ct);
        ValidateInput();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var definition = Definitions.FirstOrDefault(x => x.ConfigSettingId == Input.ConfigSettingId)
            ?? await _repo.GetDefinitionAsync(Input.ConfigSettingId, ct);

        if (definition is null)
        {
            ModelState.AddModelError(nameof(Input.ConfigSettingId), T("Select a registered config setting."));
            return Page();
        }

        ValidateConfigValue(definition);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            if (!IsEdit && Input.ScopeType == ScopeUser)
            {
                var result = await SaveScopedValuesAsync(
                    Input.ConfigUsers,
                    targetId => ToEditData(configUsrOverride: targetId),
                    ct);
                if (result.Created == 0)
                {
                    ModelState.AddModelError(
                        nameof(Input.ConfigUsers),
                        T("A value for the same setting and scope already exists."));

                    return Page();
                }

                StatusMessage = BuildScopedBatchStatus(result.Created, result.Skipped);
                return RedirectToPage("/Admin/ConfigSettings");
            }

            if (!IsEdit && Input.ScopeType == ScopePermission)
            {
                var result = await SaveScopedValuesAsync(
                    Input.ConfigPermissions,
                    targetId => ToEditData(configPermissionOverride: targetId),
                    ct);
                if (result.Created == 0)
                {
                    ModelState.AddModelError(
                        nameof(Input.ConfigPermissions),
                        T("A value for the same setting and scope already exists."));

                    return Page();
                }

                StatusMessage = BuildScopedBatchStatus(result.Created, result.Skipped);
                return RedirectToPage("/Admin/ConfigSettings");
            }

            if (!IsEdit && Input.ScopeType == ScopeRole)
            {
                var result = await SaveScopedValuesAsync(
                    Input.ConfigRoles,
                    targetId => ToEditData(configRoleOverride: targetId),
                    ct);
                if (result.Created == 0)
                {
                    ModelState.AddModelError(
                        nameof(Input.ConfigRoles),
                        T("A value for the same setting and scope already exists."));

                    return Page();
                }

                StatusMessage = BuildScopedBatchStatus(result.Created, result.Skipped);
                return RedirectToPage("/Admin/ConfigSettings");
            }

            var id = await _repo.SaveValueAsync(ToEditData(), ct);
            ClearRuntimeCacheWhenGlobal(definition);
            StatusMessage = Input.ConfigId == 0 ? T("Config setting added.") : T("Config setting updated.");
            return RedirectToPage("/Admin/ConfigSettings", new { configId = id });
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            ModelState.AddModelError(
                string.Empty,
                T("A value for the same setting and scope already exists."));

            return Page();
        }
    }

    public async Task<IActionResult> OnPostDelete(int configId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var existing = await _repo.GetValueAsync(configId, ct);
        if (existing is null)
        {
            return RedirectToPage("/Admin/ConfigSettings");
        }

        await _repo.DeleteValueAsync(configId, ct);
        if (existing.ConfigUsr is null &&
            existing.ConfigPermission is null &&
            existing.ConfigRole is null)
        {
            _configuration.ClearGlobalString(existing.ConfigCategory, existing.ConfigSetting);
        }

        StatusMessage = T("Config setting deleted.");
        return RedirectToPage("/Admin/ConfigSettings");
    }

    public string ScopeKey(ConfigSettingValueRow row)
    {
        if (row.ConfigUsr.HasValue)
        {
            return "user";
        }

        if (row.ConfigPermission.HasValue)
        {
            return "permission";
        }

        if (row.ConfigRole.HasValue)
        {
            return "role";
        }

        return "global";
    }

    public string ScopeText(ConfigSettingValueRow row)
    {
        if (row.ConfigUsr.HasValue)
        {
            return T("User");
        }

        if (row.ConfigPermission.HasValue)
        {
            return T("Permission");
        }

        if (row.ConfigRole.HasValue)
        {
            return T("Role");
        }

        return T("Global");
    }

    public string ScopeTargetText(ConfigSettingValueRow row)
    {
        if (row.ConfigUsr.HasValue)
        {
            return string.IsNullOrWhiteSpace(row.UserDisplayName)
                ? string.Format(CultureInfo.InvariantCulture, "id: {0}", row.ConfigUsr.Value)
                : $"{row.UserDisplayName} (id: {row.ConfigUsr.Value.ToString(CultureInfo.InvariantCulture)})";
        }

        if (row.ConfigPermission.HasValue)
        {
            return row.PermissionName ?? string.Format(CultureInfo.InvariantCulture, "id: {0}", row.ConfigPermission.Value);
        }

        if (row.ConfigRole.HasValue)
        {
            return row.RoleName ?? string.Format(CultureInfo.InvariantCulture, "id: {0}", row.ConfigRole.Value);
        }

        return T("All users");
    }

    public string ValuePreview(string? value)
    {
        if (value is null)
        {
            return T("NULL");
        }

        return value.Length <= 160
            ? value
            : value[..160] + "...";
    }

    private async Task LoadPageDataAsync(CancellationToken ct)
    {
        Definitions = await _repo.GetDefinitionsAsync(ct);
        Rows = await _repo.GetValuesAsync(ct);
        UserOptions = await _repo.GetUserOptionsAsync(ct);
        PermissionOptions = await _repo.GetPermissionOptionsAsync(ct);
        RoleOptions = await _repo.GetRoleOptionsAsync(ct);

        SettingOptions = Definitions
            .Select(def => Opt(
                def.ConfigSettingId.ToString(CultureInfo.InvariantCulture),
                def.IsEnabled ? def.Key : $"{def.Key} ({T("Disabled").ToLowerInvariant()})"))
            .ToArray();
    }

    private void ValidateInput()
    {
        if (Input.ConfigSettingId <= 0)
        {
            ModelState.AddModelError(nameof(Input.ConfigSettingId), T("Select a registered config setting."));
        }

        Input.ScopeType = string.IsNullOrWhiteSpace(Input.ScopeType)
            ? ScopeGlobal
            : Input.ScopeType.Trim();

        switch (Input.ScopeType)
        {
            case ScopeGlobal:
                break;
            case ScopeUser:
                if (IsEdit)
                {
                    if (!Input.ConfigUsr.HasValue)
                    {
                        ModelState.AddModelError(nameof(Input.ConfigUsr), T("Select a user."));
                    }
                }
                else
                {
                    Input.ConfigUsers = Input.ConfigUsers
                        .Where(userId => userId > 0)
                        .Distinct()
                        .ToList();

                    if (Input.ConfigUsers.Count == 0 && Input.ConfigUsr.HasValue)
                    {
                        Input.ConfigUsers.Add(Input.ConfigUsr.Value);
                    }

                    if (Input.ConfigUsers.Count == 0)
                    {
                        ModelState.AddModelError(nameof(Input.ConfigUsers), T("Select users."));
                    }
                }
                break;
            case ScopePermission:
                if (IsEdit)
                {
                    if (!Input.ConfigPermission.HasValue)
                    {
                        ModelState.AddModelError(nameof(Input.ConfigPermission), T("Select a permission."));
                    }
                }
                else
                {
                    Input.ConfigPermissions = NormalizeIds(Input.ConfigPermissions);

                    if (Input.ConfigPermissions.Count == 0 && Input.ConfigPermission.HasValue)
                    {
                        Input.ConfigPermissions.Add(Input.ConfigPermission.Value);
                    }

                    if (Input.ConfigPermissions.Count == 0)
                    {
                        ModelState.AddModelError(nameof(Input.ConfigPermissions), T("Select permissions."));
                    }
                }
                break;
            case ScopeRole:
                if (IsEdit)
                {
                    if (!Input.ConfigRole.HasValue)
                    {
                        ModelState.AddModelError(nameof(Input.ConfigRole), T("Select a role."));
                    }
                }
                else
                {
                    Input.ConfigRoles = NormalizeIds(Input.ConfigRoles);

                    if (Input.ConfigRoles.Count == 0 && Input.ConfigRole.HasValue)
                    {
                        Input.ConfigRoles.Add(Input.ConfigRole.Value);
                    }

                    if (Input.ConfigRoles.Count == 0)
                    {
                        ModelState.AddModelError(nameof(Input.ConfigRoles), T("Select roles."));
                    }
                }
                break;
            default:
                ModelState.AddModelError(nameof(Input.ScopeType), T("Select a valid scope."));
                break;
        }
    }

    private async Task<(int Created, int Skipped)> SaveScopedValuesAsync(
        IReadOnlyCollection<int> targetIds,
        Func<int, ConfigSettingValueEditData> createData,
        CancellationToken ct)
    {
        var created = 0;
        var skipped = 0;

        foreach (var targetId in targetIds)
        {
            try
            {
                await _repo.SaveValueAsync(createData(targetId), ct);
                created++;
            }
            catch (SqlException ex) when (IsUniqueConstraintViolation(ex))
            {
                skipped++;
            }
        }

        return (created, skipped);
    }

    private string BuildScopedBatchStatus(int created, int skipped)
    {
        if (skipped == 0)
        {
            return created == 1
                ? T("Config setting added.")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    T("{0} config setting values added."),
                    created);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            T("{0} config setting values added. {1} already existed and were skipped."),
            created,
            skipped);
    }

    private static List<int> NormalizeIds(IEnumerable<int> ids)
        => ids
            .Where(id => id > 0)
            .Distinct()
            .ToList();

    private void ValidateConfigValue(ConfigSettingDefinitionRow definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ValidationRegex))
        {
            return;
        }

        var value = Input.ConfigValue ?? string.Empty;

        try
        {
            if (!Regex.IsMatch(
                value,
                definition.ValidationRegex,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250)))
            {
                ModelState.AddModelError(
                    nameof(Input.ConfigValue),
                    string.Format(
                        CultureInfo.CurrentCulture,
                        T("Value does not match the expected format for {0}."),
                        definition.Key));
            }
        }
        catch (ArgumentException)
        {
            ModelState.AddModelError(
                nameof(Input.ConfigValue),
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("The validation regex for {0} is invalid. Contact an administrator."),
                    definition.Key));
        }
        catch (RegexMatchTimeoutException)
        {
            ModelState.AddModelError(
                nameof(Input.ConfigValue),
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("Validation timed out for {0}. Contact an administrator."),
                    definition.Key));
        }
    }

    private static bool IsUniqueConstraintViolation(SqlException ex)
        => ex.Number is 2601 or 2627;

    private ConfigSettingValueEditData ToEditData(
        int? configUsrOverride = null,
        int? configPermissionOverride = null,
        int? configRoleOverride = null)
    {
        int? userId = null;
        int? permissionId = null;
        int? roleId = null;

        switch (Input.ScopeType)
        {
            case ScopeUser:
                userId = configUsrOverride ?? Input.ConfigUsr;
                break;
            case ScopePermission:
                permissionId = configPermissionOverride ?? Input.ConfigPermission;
                break;
            case ScopeRole:
                roleId = configRoleOverride ?? Input.ConfigRole;
                break;
        }

        return new ConfigSettingValueEditData
        {
            ConfigId = Input.ConfigId,
            ConfigSettingId = Input.ConfigSettingId,
            ConfigValue = Input.ConfigValue,
            ConfigUsr = userId,
            ConfigPermission = permissionId,
            ConfigRole = roleId,
            ConfigPriority = Input.ConfigPriority
        };
    }

    private void ClearRuntimeCacheWhenGlobal(ConfigSettingDefinitionRow definition)
    {
        if (Input.ScopeType == ScopeGlobal)
        {
            _configuration.ClearGlobalString(definition.ConfigCategory, definition.ConfigSetting);
        }
    }

    private static InputModel ToInput(ConfigSettingValueRow row)
        => new()
        {
            ConfigId = row.ConfigId,
            ConfigSettingId = row.ConfigSettingId,
            ConfigValue = row.ConfigValue,
            ConfigUsr = row.ConfigUsr,
            ConfigUsers = row.ConfigUsr.HasValue ? [row.ConfigUsr.Value] : [],
            ConfigPermission = row.ConfigPermission,
            ConfigPermissions = row.ConfigPermission.HasValue ? [row.ConfigPermission.Value] : [],
            ConfigRole = row.ConfigRole,
            ConfigRoles = row.ConfigRole.HasValue ? [row.ConfigRole.Value] : [],
            ConfigPriority = row.ConfigPriority,
            ScopeType = row.ConfigUsr.HasValue
                ? ScopeUser
                : row.ConfigPermission.HasValue
                    ? ScopePermission
                    : row.ConfigRole.HasValue
                        ? ScopeRole
                        : ScopeGlobal
        };

    private static OptionItem Opt(string value, string label)
        => new() { Value = value, Label = label };

    public sealed class InputModel
    {
        public int ConfigId { get; set; }

        [Display(Name = "Config setting")]
        public int ConfigSettingId { get; set; }

        [Display(Name = "Value")]
        public string? ConfigValue { get; set; }

        [Display(Name = "Scope")]
        public string ScopeType { get; set; } = ScopeGlobal;

        [Display(Name = "User")]
        public int? ConfigUsr { get; set; }

        [Display(Name = "Users")]
        public List<int> ConfigUsers { get; set; } = [];

        [Display(Name = "Permission")]
        public int? ConfigPermission { get; set; }

        [Display(Name = "Permissions")]
        public List<int> ConfigPermissions { get; set; } = [];

        [Display(Name = "Role")]
        public int? ConfigRole { get; set; }

        [Display(Name = "Roles")]
        public List<int> ConfigRoles { get; set; } = [];

        [Display(Name = "Priority")]
        public int ConfigPriority { get; set; }
    }
}
