// File: OpenModulePlatform.Portal/Pages/Admin/Users/Index.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using System.Globalization;

namespace OpenModulePlatform.Portal.Pages.Admin.Users;

public sealed class IndexModel : Pages.Admin.OmpPortalPageModel
{
    private readonly OmpUserAdminRepository _repo;

    public IndexModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpUserAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<OmpUserListRow> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Users");
        Rows = await _repo.GetUsersAsync(ct);
        return Page();
    }

    public string AccountStatusText(int status)
        => T(AccountStatusLabelKey(status));

    public string FormatUtc(DateTime? value)
        => value.HasValue
            ? value.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : T("Never");

    private static string AccountStatusLabelKey(int status)
        => status switch
        {
            1 => "Active",
            2 => "Disabled",
            3 => "Deleted/reserved",
            _ => "Unknown"
        };
}
