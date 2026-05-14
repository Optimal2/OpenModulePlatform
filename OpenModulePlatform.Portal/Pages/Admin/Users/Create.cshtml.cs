// File: OpenModulePlatform.Portal/Pages/Admin/Users/Create.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;

namespace OpenModulePlatform.Portal.Pages.Admin.Users;

public sealed class CreateModel : Pages.Admin.OmpPortalPageModel
{
    private const string LocalLoginUserNameField = "LocalLogin.UserName";
    private const string LocalLoginPasswordField = "LocalLogin.Password";
    private const string LocalLoginConfirmPasswordField = "LocalLogin.ConfirmPassword";

    private readonly OmpUserAdminRepository _repo;

    public CreateModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpUserAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public LocalLoginInputModel LocalLogin { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public IReadOnlyList<OptionItem> AccountStatusOptions =>
    [
        new() { Value = "1", Label = T("Active") },
        new() { Value = "2", Label = T("Disabled") }
    ];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        Input.AccountStatus = 1;
        SetTitles("Create user");
        return Page();
    }

    public async Task<IActionResult> OnPost(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Create user");
        ValidateInput();
        ValidateLocalLoginIfRequested();
        if (!ModelState.IsValid)
        {
            ClearLocalPasswordFields();
            return Page();
        }

        var createLocalLogin = LocalLogin.HasAnyInput();
        var result = await _repo.CreateUserWithOptionalLocalLoginAsync(
            new OmpUserEditData
            {
                DisplayName = Input.DisplayName.Trim(),
                AccountStatus = Input.AccountStatus
            },
            createLocalLogin ? LocalLogin.UserName.Trim() : null,
            createLocalLogin ? LocalLogin.Password : null,
            ct);

        switch (result.Status)
        {
            case CreateUserStatus.Created:
                StatusMessage = createLocalLogin
                    ? T("User created with local login.")
                    : T("User created.");
                return RedirectToPage("/Admin/Users/Edit", new { userId = result.UserId });

            case CreateUserStatus.LocalUserNameAlreadyInUse:
                ModelState.AddModelError(LocalLoginUserNameField, T("User name is already in use."));
                break;

            case CreateUserStatus.LocalPasswordProviderMissing:
                ModelState.AddModelError(LocalLoginUserNameField, T("The local password authentication provider is missing or disabled."));
                break;

            default:
                ModelState.AddModelError(LocalLoginUserNameField, T("The OMP user account could not be created."));
                break;
        }

        ClearLocalPasswordFields();
        return Page();
    }

    private void ValidateInput()
    {
        Input.DisplayName = Input.DisplayName?.Trim() ?? string.Empty;

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

    private void ValidateLocalLoginIfRequested()
    {
        LocalLogin.UserName = LocalLogin.UserName?.Trim() ?? string.Empty;

        if (!LocalLogin.HasAnyInput())
        {
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

    private void ClearLocalPasswordFields()
    {
        LocalLogin.Password = string.Empty;
        LocalLogin.ConfirmPassword = string.Empty;
    }

    public sealed class InputModel
    {
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

        public bool HasAnyInput()
            => !string.IsNullOrWhiteSpace(UserName)
               || !string.IsNullOrEmpty(Password)
               || !string.IsNullOrEmpty(ConfirmPassword);
    }
}
