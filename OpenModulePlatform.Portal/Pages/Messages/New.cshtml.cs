using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Pages.Messages;

public sealed class NewModel : OmpSecurePageModel<PortalResource>
{
    private readonly MessageService _messages;

    public NewModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        MessageService messages)
        : base(options, rbac)
    {
        _messages = messages;
    }

    [BindProperty(SupportsGet = true)]
    [StringLength(100)]
    public string? Query { get; set; }

    [BindProperty]
    public int DirectUserId { get; set; }

    [BindProperty]
    public int[] GroupUserIds { get; set; } = [];

    [BindProperty]
    [StringLength(200)]
    public string? GroupTitle { get; set; }

    public IReadOnlyList<MessageUserOption> Users { get; private set; } = [];

    public bool CanUseMessages { get; private set; }

    public bool MessagesDisabled { get; private set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        SetTitles("New conversation");
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDirect(CancellationToken ct)
    {
        SetTitles("New conversation");

        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        if (!await _messages.IsEnabledAsync(ct))
        {
            return NotFound();
        }

        if (DirectUserId <= 0)
        {
            ModelState.AddModelError(nameof(DirectUserId), T("Select a user."));
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            var conversationId = await _messages.GetOrCreateDirectConversationAsync(userId, DirectUserId, ct);
            return RedirectToPage("/Messages/Thread", new { conversationId });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(nameof(DirectUserId), ex.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostGroup(CancellationToken ct)
    {
        SetTitles("New conversation");

        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        if (!await _messages.IsEnabledAsync(ct))
        {
            return NotFound();
        }

        if (GroupUserIds.Length == 0)
        {
            ModelState.AddModelError(nameof(GroupUserIds), T("Select users."));
            await LoadAsync(ct);
            return Page();
        }

        try
        {
            var conversationId = await _messages.CreateGroupConversationAsync(userId, GroupUserIds, GroupTitle, ct);
            return RedirectToPage("/Messages/Thread", new { conversationId });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(nameof(GroupUserIds), ex.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (!await _messages.IsEnabledAsync(ct))
        {
            MessagesDisabled = true;
            CanUseMessages = false;
            Users = [];
            return;
        }

        if (!TryGetCurrentUserId(out var userId))
        {
            CanUseMessages = false;
            Users = [];
            return;
        }

        CanUseMessages = true;
        Users = await _messages.SearchUsersAsync(userId, Query, 50, ct);
    }

    private bool TryGetCurrentUserId(out int userId)
    {
        var userIdClaim = User.FindFirstValue(OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(userIdClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId)
            && userId > 0;
    }
}
