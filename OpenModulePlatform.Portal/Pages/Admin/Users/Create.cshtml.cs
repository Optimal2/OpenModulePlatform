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
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var userId = await _repo.CreateUserAsync(
            new OmpUserEditData
            {
                DisplayName = Input.DisplayName.Trim(),
                AccountStatus = Input.AccountStatus
            },
            ct);

        StatusMessage = T("User created.");
        return RedirectToPage("/Admin/Users/Edit", new { userId });
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

    public sealed class InputModel
    {
        [Required]
        [StringLength(200)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [Display(Name = "Account status")]
        public int AccountStatus { get; set; }
    }
}
