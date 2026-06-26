// File: OpenModulePlatform.Portal/Pages/Admin/Users/Edit.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Pages.Admin.Users;

public sealed class EditModel : Pages.Admin.OmpPortalPageModel
{
    private const string LocalLoginUserNameField = "LocalLogin.UserName";
    private const string LocalLoginPasswordField = "LocalLogin.Password";
    private const string LocalLoginConfirmPasswordField = "LocalLogin.ConfirmPassword";
    private const string LocalPasswordResetPasswordField = "LocalPasswordReset.Password";
    private const string LocalPasswordResetConfirmPasswordField = "LocalPasswordReset.ConfirmPassword";
    private const string MergeAdfsDuplicateTargetUserField = "MergeAdfsDuplicate.TargetUserId";
    private const string MergeAdfsDuplicateConfirmField = "MergeAdfsDuplicate.ConfirmRepair";
    private const string PortalSettingDefinitionField = "PortalSettingInput.UserSettingDefinitionId";
    private const string PortalSettingValueField = "PortalSettingInput.SettingValue";
    private const int ValuePreviewLength = 120;

    private readonly OmpUserAdminRepository _repo;
    private readonly PortalUserSettingsAdminRepository _portalSettings;

    public EditModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpUserAdminRepository repo,
        PortalUserSettingsAdminRepository portalSettings)
        : base(options, rbac)
    {
        _repo = repo;
        _portalSettings = portalSettings;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    [StringLength(1000)]
    public string? NewAdProviderUserKey { get; set; }

    [BindProperty]
    public LocalLoginInputModel LocalLogin { get; set; } = new();

    [BindProperty]
    public LocalPasswordResetInputModel LocalPasswordReset { get; set; } = new();

    [BindProperty]
    public PortalSettingInputModel PortalSettingInput { get; set; } = new();

    [BindProperty]
    public MergeAdfsDuplicateInputModel MergeAdfsDuplicate { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public OmpUserDetail? UserRow { get; private set; }

    public PreviewMergeAdfsDuplicateUserResult? MergeAdfsDuplicatePreview { get; private set; }

    public IReadOnlyList<PortalUserSettingDefinitionRow> PortalSettingDefinitions { get; private set; } = [];

    public IReadOnlyList<PortalUserSettingValueRow> PortalSettingRows { get; private set; } = [];

    public IReadOnlyList<OptionItem> PortalSettingOptions { get; private set; } = [];

    public IReadOnlyList<OptionItem> AccountStatusOptions =>
    [
        new() { Value = "1", Label = T("Active") },
        new() { Value = "2", Label = T("Disabled") }
    ];

    public bool HasLocalPasswordLogin =>
        UserRow?.AuthLinks.Any(link => string.Equals(link.ProviderDisplayName, "lpwd", StringComparison.OrdinalIgnoreCase)) == true;

    public bool HasAdAuthLink =>
        UserRow?.AuthLinks.Any(link => string.Equals(link.ProviderDisplayName, "AD", StringComparison.OrdinalIgnoreCase)) == true;

    public string? LocalPasswordUserName =>
        UserRow?.AuthLinks.FirstOrDefault(link => string.Equals(link.ProviderDisplayName, "lpwd", StringComparison.OrdinalIgnoreCase))?.ProviderUserKey;

    public bool IsPortalSettingEdit => PortalSettingInput.OriginalUserSettingDefinitionId > 0;

    public PortalUserSettingDefinitionRow? SelectedPortalSettingDefinition =>
        PortalSettingDefinitions.FirstOrDefault(definition => definition.UserSettingDefinitionId == PortalSettingInput.UserSettingDefinitionId);

    public async Task<IActionResult> OnGet(int userId, int? portalSettingDefinitionId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (!await LoadAsync(userId, ct))
        {
            return NotFound();
        }

        PopulateInputFromLoadedUser();

        if (portalSettingDefinitionId is int selectedPortalSettingDefinitionId)
        {
            var row = PortalSettingRows.FirstOrDefault(value => value.UserSettingDefinitionId == selectedPortalSettingDefinitionId);
            if (row is null)
            {
                return NotFound();
            }

            PortalSettingInput = ToPortalSettingInput(row);
        }

        SetTitles("Edit user");
        return Page();
    }

    public async Task<IActionResult> OnPost(int userId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Edit user");
        ValidateInput();

        if (!ModelState.IsValid)
        {
            await LoadAsync(Input.UserId > 0 ? Input.UserId : userId, ct);
            return Page();
        }

        var updated = await _repo.UpdateUserAsync(
            new OmpUserEditData
            {
                UserId = Input.UserId,
                DisplayName = Input.DisplayName.Trim(),
                AccountStatus = Input.AccountStatus
            },
            ct);

        if (!updated)
        {
            return NotFound();
        }

        StatusMessage = T("User updated.");
        return RedirectToPage("/Admin/Users/Edit", new { userId = Input.UserId });
    }

    public async Task<IActionResult> OnPostLinkAdAccount(int userId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Edit user");
        if (!await LoadAsync(userId, ct))
        {
            return NotFound();
        }

        PopulateInputFromLoadedUser();
        ModelState.Clear();

        NewAdProviderUserKey = NewAdProviderUserKey?.Trim();
        if (string.IsNullOrWhiteSpace(NewAdProviderUserKey))
        {
            ModelState.AddModelError(nameof(NewAdProviderUserKey), T("Enter an AD provider user key."));
            return Page();
        }

        if (NewAdProviderUserKey.Length > 1000)
        {
            ModelState.AddModelError(nameof(NewAdProviderUserKey), T("AD provider user key must be 1000 characters or fewer."));
            return Page();
        }

        var result = await _repo.AddAdAuthLinkAsync(userId, NewAdProviderUserKey, ct);
        switch (result.Status)
        {
            case AddAuthLinkStatus.Added:
                StatusMessage = T("AD account linked.");
                return RedirectToPage("/Admin/Users/Edit", new { userId });

            case AddAuthLinkStatus.AlreadyLinkedToThisUser:
                ModelState.AddModelError(nameof(NewAdProviderUserKey), T("This AD account is already linked to this user."));
                return Page();

            case AddAuthLinkStatus.AlreadyLinkedToAnotherUser:
                ModelState.AddModelError(
                    nameof(NewAdProviderUserKey),
                    string.Format(
                        CultureInfo.CurrentCulture,
                        T("This AD account is already linked to user {0}."),
                        result.ExistingUserId?.ToString(CultureInfo.InvariantCulture) ?? "?"));
                return Page();

            case AddAuthLinkStatus.ProviderMissing:
                ModelState.AddModelError(nameof(NewAdProviderUserKey), T("The AD authentication provider is missing."));
                return Page();

            default:
                ModelState.AddModelError(nameof(NewAdProviderUserKey), T("The AD account could not be linked."));
                return Page();
        }
    }

    public async Task<IActionResult> OnPostAddLocalLogin(int userId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Edit user");
        if (!await LoadAsync(userId, ct))
        {
            return NotFound();
        }

        PopulateInputFromLoadedUser();
        ModelState.Clear();
        ValidateLocalLogin();
        if (!ModelState.IsValid)
        {
            ClearLocalPasswordFields();
            return Page();
        }

        var result = await _repo.AddLocalPasswordLoginAsync(
            userId,
            LocalLogin.UserName.Trim(),
            LocalLogin.Password,
            ct);

        switch (result.Status)
        {
            case AddLocalPasswordLoginStatus.Added:
                StatusMessage = T("Local login added.");
                return RedirectToPage("/Admin/Users/Edit", new { userId });

            case AddLocalPasswordLoginStatus.UserAlreadyHasLocalLogin:
                ModelState.AddModelError(LocalLoginUserNameField, T("Local login already exists."));
                break;

            case AddLocalPasswordLoginStatus.UserNameAlreadyInUse:
                ModelState.AddModelError(LocalLoginUserNameField, T("User name is already in use."));
                break;

            case AddLocalPasswordLoginStatus.ProviderMissing:
                ModelState.AddModelError(LocalLoginUserNameField, T("The local password authentication provider is missing or disabled."));
                break;

            case AddLocalPasswordLoginStatus.UserMissing:
                return NotFound();

            default:
                ModelState.AddModelError(LocalLoginUserNameField, T("The local login could not be added."));
                break;
        }

        ClearLocalPasswordFields();
        return Page();
    }

    public async Task<IActionResult> OnPostResetLocalPassword(int userId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Edit user");
        if (!await LoadAsync(userId, ct))
        {
            return NotFound();
        }

        PopulateInputFromLoadedUser();
        ModelState.Clear();
        ValidateLocalPasswordReset();
        if (!ModelState.IsValid)
        {
            ClearLocalPasswordFields();
            return Page();
        }

        var result = await _repo.ResetLocalPasswordAsync(userId, LocalPasswordReset.Password, ct);
        switch (result)
        {
            case ResetLocalPasswordResult.Reset:
                StatusMessage = T("Local password reset.");
                return RedirectToPage("/Admin/Users/Edit", new { userId });

            case ResetLocalPasswordResult.ProviderMissing:
                ModelState.AddModelError(LocalPasswordResetPasswordField, T("The local password authentication provider is missing or disabled."));
                break;

            case ResetLocalPasswordResult.LocalLoginMissing:
                ModelState.AddModelError(LocalPasswordResetPasswordField, T("Local login is missing."));
                break;

            default:
                ModelState.AddModelError(LocalPasswordResetPasswordField, T("The local password could not be reset."));
                break;
        }

        ClearLocalPasswordFields();
        return Page();
    }

    public async Task<IActionResult> OnPostSavePortalSetting(int userId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Edit user");
        if (!await LoadAsync(userId, ct))
        {
            return NotFound();
        }

        PopulateInputFromLoadedUser();
        ModelState.Clear();

        var definition = ValidatePortalSettingInput();
        if (definition is null)
        {
            return Page();
        }

        var editData = ToPortalSettingEditData(definition);
        var saved = await _portalSettings.SaveValueAsync(userId, editData, ct);
        if (!saved)
        {
            ModelState.AddModelError(PortalSettingDefinitionField, T("This portal setting is not available."));
            return Page();
        }

        StatusMessage = IsPortalSettingEdit
            ? T("Portal setting updated.")
            : T("Portal setting added.");
        return RedirectToPage("/Admin/Users/Edit", new { userId });
    }

    public async Task<IActionResult> OnPostDeletePortalSetting(int userId, int userSettingDefinitionId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (!await LoadAsync(userId, ct))
        {
            return NotFound();
        }

        await _portalSettings.DeleteValueAsync(userId, userSettingDefinitionId, ct);

        StatusMessage = T("Portal setting deleted.");
        return RedirectToPage("/Admin/Users/Edit", new { userId });
    }

    public async Task<IActionResult> OnPostRemoveAuthLink(int userId, int userAuthId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var result = await _repo.RemoveAuthLinkAsync(userId, userAuthId, ct);
        switch (result.Status)
        {
            case RemoveAuthLinkStatus.Removed:
                StatusMessage = AuthLinkRemovedMessage(result.ProviderDisplayName);
                return RedirectToPage("/Admin/Users/Edit", new { userId });

            case RemoveAuthLinkStatus.AuthLinkMissing:
                StatusMessage = T("Auth link not found.");
                return RedirectToPage("/Admin/Users/Edit", new { userId });

            default:
                StatusMessage = T("The auth link could not be removed.");
                return RedirectToPage("/Admin/Users/Edit", new { userId });
        }
    }

    public async Task<IActionResult> OnPostMigrateAdRights(int userId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        var result = await _repo.MigrateAdUserRoleAssignmentsToOmpUserAsync(userId, ct);
        switch (result.Status)
        {
            case MigrateAdUserRoleAssignmentsStatus.Migrated:
                StatusMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    await TWithBrandingAsync("Moved {0} direct AD-user role assignments to this OMP user. Created {1} OMP-user role assignments.", ct),
                    result.RemovedAdUserAssignmentCount.ToString(CultureInfo.InvariantCulture),
                    result.CreatedOmpUserAssignmentCount.ToString(CultureInfo.InvariantCulture));
                return RedirectToPage("/Admin/Users/Edit", new { userId });

            case MigrateAdUserRoleAssignmentsStatus.NoAssignments:
                StatusMessage = T("No direct AD-user rights are available to move.");
                return RedirectToPage("/Admin/Users/Edit", new { userId });

            case MigrateAdUserRoleAssignmentsStatus.NoAdLinks:
                StatusMessage = await TWithBrandingAsync("This OMP user has no AD links.", ct);
                return RedirectToPage("/Admin/Users/Edit", new { userId });

            case MigrateAdUserRoleAssignmentsStatus.UserMissing:
                return NotFound();

            default:
                StatusMessage = T("The AD rights could not be moved.");
                return RedirectToPage("/Admin/Users/Edit", new { userId });
        }
    }

    public async Task<IActionResult> OnPostPreviewMergeAdfsDuplicate(int userId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Edit user");
        if (!await LoadAsync(userId, ct))
        {
            return NotFound();
        }

        PopulateInputFromLoadedUser();
        ModelState.Clear();

        if (!ValidateMergeAdfsDuplicateTarget())
        {
            return Page();
        }

        MergeAdfsDuplicatePreview = await _repo.PreviewMergeAdfsDuplicateUserAsync(
            duplicateUserId: userId,
            targetUserId: MergeAdfsDuplicate.TargetUserId,
            ct);

        return Page();
    }

    public async Task<IActionResult> OnPostMergeAdfsDuplicate(int userId, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Edit user");
        if (!await LoadAsync(userId, ct))
        {
            return NotFound();
        }

        PopulateInputFromLoadedUser();
        ModelState.Clear();

        var hasValidTarget = ValidateMergeAdfsDuplicateTarget();
        if (!MergeAdfsDuplicate.ConfirmRepair)
        {
            ModelState.AddModelError(MergeAdfsDuplicateConfirmField, T("Confirm that ADFS links should be moved and this duplicate user disabled."));
        }

        if (!hasValidTarget || !ModelState.IsValid)
        {
            if (hasValidTarget)
            {
                MergeAdfsDuplicatePreview = await _repo.PreviewMergeAdfsDuplicateUserAsync(
                    duplicateUserId: userId,
                    targetUserId: MergeAdfsDuplicate.TargetUserId,
                    ct);
            }

            return Page();
        }

        var result = await _repo.MergeAdfsDuplicateUserAsync(
            duplicateUserId: userId,
            targetUserId: MergeAdfsDuplicate.TargetUserId,
            actor: BuildMergeActor(),
            ct);

        if (result.Status == MergeAdfsDuplicateUserStatus.Merged)
        {
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                T("ADFS duplicate repair completed. Moved {0} ADFS auth link(s) to target user {1} and disabled duplicate user {2}."),
                result.MovedAuthLinkCount.ToString(CultureInfo.InvariantCulture),
                result.TargetUserId.ToString(CultureInfo.InvariantCulture),
                result.DuplicateUserId.ToString(CultureInfo.InvariantCulture));
            return RedirectToPage("/Admin/Users/Edit", new { userId });
        }

        MergeAdfsDuplicatePreview = await _repo.PreviewMergeAdfsDuplicateUserAsync(
            duplicateUserId: userId,
            targetUserId: MergeAdfsDuplicate.TargetUserId,
            ct);
        ModelState.AddModelError(MergeAdfsDuplicateTargetUserField, MergeAdfsDuplicateStatusText(result.Status));
        return Page();
    }

    public string AccountStatusText(int status)
        => T(AccountStatusLabelKey(status));

    public string FormatUtc(DateTime? value)
        => value.HasValue
            ? value.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : T("Never");

    public string PortalSettingValueKindText(byte valueKind)
        => valueKind switch
        {
            PortalUserSettingsAdminRepository.IntValueKind => T("Integer"),
            PortalUserSettingsAdminRepository.StringValueKind => T("String"),
            _ => T("Unknown")
        };

    public string PortalSettingValueText(PortalUserSettingValueRow row)
        => row.ValueKind == PortalUserSettingsAdminRepository.IntValueKind
            ? row.IntValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
            : ValuePreview(row.StringValue);

    public string PortalSettingDefaultText(PortalUserSettingDefinitionRow definition)
        => definition.ValueKind == PortalUserSettingsAdminRepository.IntValueKind
            ? definition.DefaultIntValue?.ToString(CultureInfo.InvariantCulture) ?? T("None")
            : ValuePreview(definition.DefaultStringValue);

    public string PortalSettingDefaultText(PortalUserSettingValueRow row)
        => row.ValueKind == PortalUserSettingsAdminRepository.IntValueKind
            ? row.DefaultIntValue?.ToString(CultureInfo.InvariantCulture) ?? T("None")
            : ValuePreview(row.DefaultStringValue);

    public string RoleSourceText(OmpUserRoleRow row)
        => string.Equals(row.PrincipalType, "OmpUser", StringComparison.OrdinalIgnoreCase)
            ? T("OMP user")
            : string.Equals(row.PrincipalType, "ADUser", StringComparison.OrdinalIgnoreCase)
                ? T("Linked AD account")
                : row.PrincipalType;

    public string MergeAdfsDuplicateStatusText(MergeAdfsDuplicateUserStatus status)
        => status switch
        {
            MergeAdfsDuplicateUserStatus.Merged => T("Repair completed."),
            MergeAdfsDuplicateUserStatus.PreviewOnly => T("Preview is ready. Review the auth links, then confirm the repair."),
            MergeAdfsDuplicateUserStatus.TargetMissing => T("The target user was not found."),
            MergeAdfsDuplicateUserStatus.DuplicateMissing => T("The duplicate user was not found."),
            MergeAdfsDuplicateUserStatus.SameUser => T("The target user and duplicate user must be different users."),
            MergeAdfsDuplicateUserStatus.SystemUserNotAllowed => T("System or reserved users cannot be repaired here."),
            MergeAdfsDuplicateUserStatus.TargetNotActive => T("The target user must be active."),
            MergeAdfsDuplicateUserStatus.DuplicateAlreadyDeleted => T("The duplicate user is already deleted or has an unsupported account status."),
            MergeAdfsDuplicateUserStatus.AdfsProviderMissing => T("The ADFS authentication provider was not found."),
            MergeAdfsDuplicateUserStatus.NoEnabledAdfsLinks => T("The duplicate user has no enabled ADFS auth links to move."),
            MergeAdfsDuplicateUserStatus.DuplicateHasEnabledNonAdfsLinks => T("The duplicate user has enabled non-ADFS auth links. Remove or review those links before running this repair."),
            MergeAdfsDuplicateUserStatus.IntegrityAnomaly => T("Conflicting ADFS auth links were found. No repair can run until the auth-link data is reviewed."),
            MergeAdfsDuplicateUserStatus.ConcurrencyConflict => T("The duplicate user's ADFS auth links changed during the repair. Preview again before retrying."),
            _ => T("The ADFS duplicate repair could not be completed.")
        };

    public string MergeUserSummaryText(MergeAdfsUserSummary? user)
        => user is null
            ? T("Not found")
            : string.Format(
                CultureInfo.CurrentCulture,
                T("{0} (id: {1}, status: {2})"),
                user.DisplayName,
                user.UserId.ToString(CultureInfo.InvariantCulture),
                AccountStatusText(user.AccountStatus));

    private async Task<bool> LoadAsync(int userId, CancellationToken ct)
    {
        UserRow = await _repo.GetUserAsync(userId, ct);
        if (UserRow is null)
        {
            return false;
        }

        PortalSettingDefinitions = await _portalSettings.GetDefinitionsAsync(ct);
        PortalSettingRows = await _portalSettings.GetValuesForUserAsync(userId, ct);
        PortalSettingOptions = PortalSettingDefinitions
            .Select(definition => new OptionItem
            {
                Value = definition.UserSettingDefinitionId.ToString(CultureInfo.InvariantCulture),
                Label = definition.IsEnabled
                    ? $"{definition.Key} ({PortalSettingValueKindText(definition.ValueKind)})"
                    : $"{definition.Key} ({PortalSettingValueKindText(definition.ValueKind)}, {T("disabled")})"
            })
            .ToArray();

        return true;
    }

    private void PopulateInputFromLoadedUser()
    {
        if (UserRow is null)
        {
            return;
        }

        Input = new InputModel
        {
            UserId = UserRow.UserId,
            DisplayName = UserRow.DisplayName,
            AccountStatus = UserRow.AccountStatus
        };
    }

    private void ValidateInput()
    {
        Input.DisplayName = Input.DisplayName?.Trim() ?? string.Empty;

        if (Input.UserId <= 0)
        {
            ModelState.AddModelError(nameof(Input.UserId), T("User ID is required."));
        }

        if (string.IsNullOrWhiteSpace(Input.DisplayName))
        {
            ModelState.AddModelError(nameof(Input.DisplayName), T("Display name is required."));
        }

        if (Input.DisplayName.Length > 200)
        {
            ModelState.AddModelError(nameof(Input.DisplayName), T("Display name must be 200 characters or fewer."));
        }

        if (Input.AccountStatus is not (1 or 2))
        {
            ModelState.AddModelError(nameof(Input.AccountStatus), T("Select an account status."));
        }
    }

    private void ValidateLocalLogin()
    {
        LocalLogin.UserName = LocalLogin.UserName?.Trim() ?? string.Empty;

        if (HasLocalPasswordLogin)
        {
            ModelState.AddModelError(LocalLoginUserNameField, T("Local login already exists."));
            return;
        }

        if (string.IsNullOrWhiteSpace(LocalLogin.UserName))
        {
            ModelState.AddModelError(LocalLoginUserNameField, T("User name is required."));
        }

        if (LocalLogin.UserName.Length > 256)
        {
            ModelState.AddModelError(LocalLoginUserNameField, T("User name must be 256 characters or fewer."));
        }

        if (string.IsNullOrEmpty(LocalLogin.Password))
        {
            ModelState.AddModelError(LocalLoginPasswordField, T("Password is required."));
        }
        else if (LocalLogin.Password.Length < 8)
        {
            ModelState.AddModelError(LocalLoginPasswordField, T("Password must be at least 8 characters."));
        }

        if (!string.Equals(LocalLogin.Password, LocalLogin.ConfirmPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError(LocalLoginConfirmPasswordField, T("Password and confirmation password do not match."));
        }
    }

    private void ValidateLocalPasswordReset()
    {
        if (!HasLocalPasswordLogin)
        {
            ModelState.AddModelError(LocalPasswordResetPasswordField, T("Local login is missing."));
            return;
        }

        if (string.IsNullOrEmpty(LocalPasswordReset.Password))
        {
            ModelState.AddModelError(LocalPasswordResetPasswordField, T("Password is required."));
        }
        else if (LocalPasswordReset.Password.Length < 8)
        {
            ModelState.AddModelError(LocalPasswordResetPasswordField, T("Password must be at least 8 characters."));
        }

        if (!string.Equals(LocalPasswordReset.Password, LocalPasswordReset.ConfirmPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError(LocalPasswordResetConfirmPasswordField, T("Password and confirmation password do not match."));
        }
    }

    private PortalUserSettingDefinitionRow? ValidatePortalSettingInput()
    {
        var definitionId = IsPortalSettingEdit
            ? PortalSettingInput.OriginalUserSettingDefinitionId
            : PortalSettingInput.UserSettingDefinitionId;

        if (definitionId <= 0)
        {
            ModelState.AddModelError(PortalSettingDefinitionField, T("Select a portal setting."));
            return null;
        }

        var definition = PortalSettingDefinitions.FirstOrDefault(row => row.UserSettingDefinitionId == definitionId);
        if (definition is null)
        {
            ModelState.AddModelError(PortalSettingDefinitionField, T("This portal setting is not available."));
            return null;
        }

        if (!IsPortalSettingEdit && PortalSettingRows.Any(row => row.UserSettingDefinitionId == definition.UserSettingDefinitionId))
        {
            ModelState.AddModelError(PortalSettingDefinitionField, T("A value for this portal setting already exists for this user."));
            return null;
        }

        PortalSettingInput.UserSettingDefinitionId = definition.UserSettingDefinitionId;
        PortalSettingInput.ValueKind = definition.ValueKind;

        if (definition.ValueKind == PortalUserSettingsAdminRepository.IntValueKind)
        {
            if (!int.TryParse(PortalSettingInput.SettingValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                ModelState.AddModelError(PortalSettingValueField, T("Enter a valid integer value."));
                return null;
            }

            PortalSettingInput.IntValue = intValue;
            PortalSettingInput.StringValue = null;
            return definition;
        }

        if (definition.ValueKind == PortalUserSettingsAdminRepository.StringValueKind)
        {
            PortalSettingInput.StringValue = PortalSettingInput.SettingValue ?? string.Empty;
            PortalSettingInput.IntValue = null;
            return definition;
        }

        ModelState.AddModelError(PortalSettingDefinitionField, T("This portal setting uses an unsupported value type."));
        return null;
    }

    private PortalSettingInputModel ToPortalSettingInput(PortalUserSettingValueRow row)
    {
        var value = row.ValueKind == PortalUserSettingsAdminRepository.IntValueKind
            ? row.IntValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
            : row.StringValue ?? string.Empty;

        return new PortalSettingInputModel
        {
            UserSettingDefinitionId = row.UserSettingDefinitionId,
            OriginalUserSettingDefinitionId = row.UserSettingDefinitionId,
            ValueKind = row.ValueKind,
            SettingValue = value,
            IntValue = row.IntValue,
            StringValue = row.StringValue
        };
    }

    private PortalUserSettingValueEditData ToPortalSettingEditData(PortalUserSettingDefinitionRow definition)
        => new(
            definition.UserSettingDefinitionId,
            definition.ValueKind,
            PortalSettingInput.IntValue,
            PortalSettingInput.StringValue);

    private void ClearLocalPasswordFields()
    {
        LocalLogin.Password = string.Empty;
        LocalLogin.ConfirmPassword = string.Empty;
        LocalPasswordReset.Password = string.Empty;
        LocalPasswordReset.ConfirmPassword = string.Empty;
    }

    private string AuthLinkRemovedMessage(string? providerDisplayName)
    {
        if (string.Equals(providerDisplayName, "AD", StringComparison.OrdinalIgnoreCase))
        {
            return T("AD link removed.");
        }

        if (string.Equals(providerDisplayName, "lpwd", StringComparison.OrdinalIgnoreCase))
        {
            return T("Local login removed.");
        }

        return T("Auth link removed.");
    }

    private bool ValidateMergeAdfsDuplicateTarget()
    {
        if (MergeAdfsDuplicate.TargetUserId <= 0)
        {
            ModelState.AddModelError(MergeAdfsDuplicateTargetUserField, T("Enter the target user ID to keep."));
            return false;
        }

        if (UserRow is not null && MergeAdfsDuplicate.TargetUserId == UserRow.UserId)
        {
            ModelState.AddModelError(MergeAdfsDuplicateTargetUserField, T("The target user and duplicate user must be different users."));
            return false;
        }

        return true;
    }

    private string BuildMergeActor()
    {
        var userId = User.FindFirstValue(OmpAuthDefaults.UserIdClaimType);
        var name = User.Identity?.Name;

        if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(name))
        {
            return $"OmpUser|{userId}|{name.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(userId))
        {
            return $"OmpUser|{userId}";
        }

        return string.IsNullOrWhiteSpace(name) ? "PortalAdmin" : name.Trim();
    }

    private static string AccountStatusLabelKey(int status)
        => status switch
        {
            1 => "Active",
            2 => "Disabled",
            3 => "Deleted/reserved",
            _ => "Unknown"
        };

    private static string ValuePreview(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= ValuePreviewLength
            ? normalized
            : normalized[..ValuePreviewLength] + "...";
    }

    public sealed class InputModel
    {
        public int UserId { get; set; }

        [Required]
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [Display(Name = "Account status")]
        public int AccountStatus { get; set; }
    }

    public sealed class LocalLoginInputModel
    {
        [Display(Name = "User name")]
        public string UserName { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public sealed class LocalPasswordResetInputModel
    {
        [DataType(DataType.Password)]
        [Display(Name = "New password")]
        public string Password { get; set; } = string.Empty;

        [DataType(DataType.Password)]
        [Display(Name = "Confirm new password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public sealed class PortalSettingInputModel
    {
        [Display(Name = "Portal setting")]
        public int UserSettingDefinitionId { get; set; }

        public int OriginalUserSettingDefinitionId { get; set; }

        public byte ValueKind { get; set; }

        [Display(Name = "Setting value")]
        public string? SettingValue { get; set; }

        public int? IntValue { get; set; }

        public string? StringValue { get; set; }
    }

    public sealed class MergeAdfsDuplicateInputModel
    {
        [Display(Name = "Target user ID")]
        public int TargetUserId { get; set; }

        [Display(Name = "Confirm repair")]
        public bool ConfirmRepair { get; set; }
    }
}
