// File: OpenModulePlatform.Portal/Pages/Admin/Users/Edit.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace OpenModulePlatform.Portal.Pages.Admin.Users;

public sealed class EditModel : Pages.Admin.OmpPortalPageModel
{
    private readonly OmpUserAdminRepository _repo;

    public EditModel(
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
    [StringLength(1000)]
    public string? NewAdProviderUserKey { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public OmpUserDetail? UserRow { get; private set; }

    public IReadOnlyList<OptionItem> AccountStatusOptions =>
    [
        new() { Value = "1", Label = T("Active") },
        new() { Value = "2", Label = T("Disabled") }
    ];

    public async Task<IActionResult> OnGet(int userId, CancellationToken ct)
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

        Input = new InputModel
        {
            UserId = UserRow!.UserId,
            DisplayName = UserRow.DisplayName,
            AccountStatus = UserRow.AccountStatus
        };

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

        Input = new InputModel
        {
            UserId = UserRow!.UserId,
            DisplayName = UserRow.DisplayName,
            AccountStatus = UserRow.AccountStatus
        };

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

    public string AccountStatusText(int status)
        => T(AccountStatusLabelKey(status));

    public string FormatUtc(DateTime? value)
        => value.HasValue
            ? value.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : T("Never");

    private async Task<bool> LoadAsync(int userId, CancellationToken ct)
    {
        UserRow = await _repo.GetUserAsync(userId, ct);
        return UserRow is not null;
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

    private static string AccountStatusLabelKey(int status)
        => status switch
        {
            1 => "Active",
            2 => "Disabled",
            3 => "Deleted/reserved",
            _ => "Unknown"
        };

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
}
