using OpenModulePlatform.Web.IframeModule.Services;
using OpenModulePlatform.Web.IframeModule.ViewModels;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.IframeModule.Pages;

public sealed class IndexModel : IframeModulePageModel
{
    private readonly IframeModuleRepository _repo;

    public IndexModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        IframeModuleRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<IframeUrlRow> UrlSlots { get; private set; } = Array.Empty<IframeUrlRow>();

    public IframeUrlRow? SelectedUrl { get; private set; }

    public int SelectedSlot { get; private set; } = 1;

    public bool HasUrls => UrlSlots.Count > 0;

    public async Task<IActionResult> OnGet(int? slot, CancellationToken ct)
    {
        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("Iframe module");

        var activeRoleName = await GetActiveRoleNameAsync(ct);
        var urls = await _repo.GetTopUrlsAsync(activeRoleName, ct);

        UrlSlots = urls
            .Where(x => IsSafeIframeUrl(x.Url))
            .ToArray();

        if (UrlSlots.Count == 0)
        {
            SelectedSlot = 0;
            SelectedUrl = null;
            return Page();
        }

        SelectedSlot = slot.GetValueOrDefault(1);
        if (SelectedSlot < 1 || SelectedSlot > UrlSlots.Count)
        {
            SelectedSlot = 1;
        }

        SelectedUrl = UrlSlots[SelectedSlot - 1];
        return Page();
    }

    private static bool IsSafeIframeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (url.StartsWith("/", StringComparison.Ordinal))
        {
            return true;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return false;
        }

        return string.Equals(absolute.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}
