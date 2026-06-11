using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Web;
using System.Globalization;
using System.Security.Claims;

namespace OpenModulePlatform.Portal.Pages.Messages;

public sealed class IndexModel : OmpSecurePageModel<PortalResource>
{
    private readonly MessageService _messages;

    public IndexModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        MessageService messages)
        : base(options, rbac)
    {
        _messages = messages;
    }

    public IReadOnlyList<MessageConversationSummary> Rows { get; private set; } = [];

    public bool CanUseMessages { get; private set; }

    public bool MessagesDisabled { get; private set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        SetTitles("Messages");

        if (!await _messages.IsEnabledAsync(ct))
        {
            MessagesDisabled = true;
            CanUseMessages = false;
            return Page();
        }

        if (!TryGetCurrentUserId(out var userId))
        {
            CanUseMessages = false;
            return Page();
        }

        CanUseMessages = true;
        Rows = await _messages.GetConversationsForUserAsync(userId, ct);
        return Page();
    }

    private bool TryGetCurrentUserId(out int userId)
    {
        var userIdClaim = User.FindFirstValue(OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(userIdClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId)
            && userId > 0;
    }
}
