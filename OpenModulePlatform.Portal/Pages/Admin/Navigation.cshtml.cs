// File: OpenModulePlatform.Portal/Pages/Admin/Navigation.cshtml.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class NavigationModel : OmpPortalPageModel
{
    private readonly LinkBoxRepository _linkBoxes;

    public NavigationModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        LinkBoxRepository linkBoxes)
        : base(options, rbac)
    {
        _linkBoxes = linkBoxes;
    }

    // Registered boxes plus any box keys that only exist as stray item rows.
    public IReadOnlyList<LinkBoxRow> Boxes { get; private set; } = Array.Empty<LinkBoxRow>();
    public string SelectedBoxKey { get; private set; } = PortalLinkBoxes.AdminQuickLinksKey;
    public LinkBoxRow? SelectedBox { get; private set; }
    public IReadOnlyList<LinkBoxItemRow> Links { get; private set; } = Array.Empty<LinkBoxItemRow>();
    public IReadOnlyList<string> PermissionNames { get; private set; } = Array.Empty<string>();

    [BindProperty]
    public BoxSettingsInput BoxInput { get; set; } = new();

    [BindProperty]
    public LinkInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(string? box, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(box, ct);
        SetTitles("Navigation");
        return Page();
    }

    public async Task<IActionResult> OnPostSaveBoxAsync(string box, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(box, ct);
        ValidateBoxInput();
        if (!ModelState.IsValid)
        {
            SetTitles("Navigation");
            return Page();
        }

        await _linkBoxes.UpsertBoxAsync(
            SelectedBoxKey,
            BoxInput.Title.Trim(),
            string.IsNullOrWhiteSpace(BoxInput.RequiredPermission) ? null : BoxInput.RequiredPermission.Trim(),
            ct);

        StatusMessage = T("Box saved.");
        return RedirectToPage("Navigation", new { box = SelectedBoxKey });
    }

    public async Task<IActionResult> OnPostAddAsync(string box, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await LoadAsync(box, ct);
        ValidateLinkInput();
        if (!ModelState.IsValid)
        {
            SetTitles("Navigation");
            return Page();
        }

        // Adding to an unregistered (stray) box key registers the shell so the
        // box shows up as a first-class entry from then on.
        if (SelectedBox is null)
        {
            await _linkBoxes.UpsertBoxAsync(SelectedBoxKey, SelectedBoxKey, null, ct);
        }

        await _linkBoxes.AddItemAsync(
            SelectedBoxKey,
            Input.Label.Trim(),
            Input.Url.Trim(),
            string.IsNullOrWhiteSpace(Input.Group) ? null : Input.Group.Trim(),
            string.IsNullOrWhiteSpace(Input.RequiredPermission) ? null : Input.RequiredPermission.Trim(),
            ct);

        StatusMessage = T("Link added.");
        return RedirectToPage("Navigation", new { box = SelectedBoxKey });
    }

    public async Task<IActionResult> OnPostDeleteAsync(string box, long id, CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        await _linkBoxes.DeleteItemAsync(id, ct);
        StatusMessage = T("Link removed.");
        return RedirectToPage("Navigation", new { box });
    }

    private async Task LoadAsync(string? box, CancellationToken ct)
    {
        var registered = await _linkBoxes.GetBoxesAsync(ct);
        var boxes = new List<LinkBoxRow>(registered);
        foreach (var strayKey in await _linkBoxes.GetBoxKeysAsync(ct))
        {
            if (boxes.All(item => !string.Equals(item.BoxKey, strayKey, StringComparison.OrdinalIgnoreCase)))
            {
                boxes.Add(new LinkBoxRow { BoxKey = strayKey, Title = strayKey });
            }
        }

        Boxes = boxes;
        SelectedBoxKey = boxes.FirstOrDefault(item => string.Equals(item.BoxKey, box, StringComparison.OrdinalIgnoreCase))?.BoxKey
            ?? boxes.FirstOrDefault()?.BoxKey
            ?? PortalLinkBoxes.AdminQuickLinksKey;
        SelectedBox = registered.FirstOrDefault(item => string.Equals(item.BoxKey, SelectedBoxKey, StringComparison.OrdinalIgnoreCase));
        Links = await _linkBoxes.GetItemsAsync(SelectedBoxKey, ct);
        PermissionNames = await _linkBoxes.GetPermissionNamesAsync(ct);

        if (!Request.HasFormContentType || !Request.Form.ContainsKey("BoxInput.Title"))
        {
            BoxInput = new BoxSettingsInput
            {
                Title = SelectedBox?.Title ?? SelectedBoxKey,
                RequiredPermission = SelectedBox?.RequiredPermission
            };
        }
    }

    private void ValidateBoxInput()
    {
        if (string.IsNullOrWhiteSpace(BoxInput.Title))
        {
            ModelState.AddModelError("BoxInput.Title", T("Title is required."));
        }
        else if (BoxInput.Title.Trim().Length > 200)
        {
            ModelState.AddModelError("BoxInput.Title", T("Title can be at most 200 characters."));
        }

        ValidatePermission(BoxInput.RequiredPermission, "BoxInput.RequiredPermission");
    }

    private void ValidateLinkInput()
    {
        if (string.IsNullOrWhiteSpace(Input.Label))
        {
            ModelState.AddModelError("Input.Label", T("Label is required."));
        }
        else if (Input.Label.Trim().Length > 200)
        {
            ModelState.AddModelError("Input.Label", T("Label can be at most 200 characters."));
        }

        var url = Input.Url?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            ModelState.AddModelError("Input.Url", T("URL is required."));
        }
        else if (url.Length > 400)
        {
            ModelState.AddModelError("Input.Url", T("URL can be at most 400 characters."));
        }
        else if (!IsSafeRelativeUrl(url))
        {
            ModelState.AddModelError("Input.Url", T("Only relative URLs within this site are allowed, e.g. /admin/hosts."));
        }

        if (!string.IsNullOrWhiteSpace(Input.Group) && Input.Group.Trim().Length > 100)
        {
            ModelState.AddModelError("Input.Group", T("Group can be at most 100 characters."));
        }

        ValidatePermission(Input.RequiredPermission, "Input.RequiredPermission");
    }

    private void ValidatePermission(string? permission, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(permission)
            && !PermissionNames.Contains(permission.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(fieldName, T("Unknown permission."));
        }
    }

    // Same-origin guard: only app-relative paths are storable, so a stored
    // link can never navigate off-site or smuggle a scheme (open redirect /
    // javascript: XSS). Rendering prefixes "~" for virtual app paths.
    private static bool IsSafeRelativeUrl(string url)
    {
        return url.StartsWith('/')
            && !url.StartsWith("//", StringComparison.Ordinal)
            && !url.StartsWith("/\\", StringComparison.Ordinal)
            && Uri.TryCreate(url, UriKind.Relative, out _);
    }

    public sealed class BoxSettingsInput
    {
        public string Title { get; set; } = string.Empty;
        public string? RequiredPermission { get; set; }
    }

    public sealed class LinkInput
    {
        public string Label { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? Group { get; set; }
        public string? RequiredPermission { get; set; }
    }
}
