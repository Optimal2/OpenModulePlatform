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

public sealed class ThreadModel : OmpSecurePageModel<PortalResource>
{
    private const string MessagesPartialName = "_ThreadMessages";

    private readonly MessageService _messages;

    public ThreadModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        MessageService messages)
        : base(options, rbac)
    {
        _messages = messages;
    }

    public MessageConversationDetail? Conversation { get; private set; }

    public IReadOnlyList<MessageRow> Rows { get; private set; } = [];

    public bool CanUseMessages { get; private set; }

    public bool MessagesDisabled { get; private set; }

    public bool HasMoreMessages { get; private set; }

    public long LatestMessageId => Rows.Count == 0 ? 0 : Rows[^1].MessageId;

    public bool IsHistoryView { get; private set; }

    [BindProperty]
    [StringLength(4000)]
    public string? MessageContent { get; set; }

    [BindProperty]
    public List<IFormFile> Attachments { get; set; } = [];

    [BindProperty]
    public int? RestoreScrollTop { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGet(long conversationId, long? beforeMessageId, CancellationToken ct)
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
        var loaded = await LoadAsync(userId, conversationId, beforeMessageId, markRead: beforeMessageId is null, ct);
        if (!loaded)
        {
            return Forbid();
        }

        return Page();
    }

    public async Task<IActionResult> OnGetMessages(long conversationId, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        if (!await _messages.IsEnabledAsync(ct))
        {
            return NotFound();
        }

        var loaded = await LoadAsync(userId, conversationId, beforeMessageId: null, markRead: true, ct);
        if (!loaded)
        {
            return Forbid();
        }

        Response.Headers.CacheControl = "no-store";
        return Partial(MessagesPartialName, this);
    }

    public async Task<IActionResult> OnPostSend(long conversationId, CancellationToken ct)
    {
        SetTitles("Messages");
        var isAjaxRequest = IsAjaxRequest();

        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        if (!await _messages.IsEnabledAsync(ct))
        {
            return NotFound();
        }

        try
        {
            await _messages.SendMessageAsync(userId, conversationId, MessageContent, Attachments, ct);
            if (isAjaxRequest)
            {
                await LoadAsync(userId, conversationId, beforeMessageId: null, markRead: true, ct);
                Response.Headers.CacheControl = "no-store";
                return Partial(MessagesPartialName, this);
            }

            return RedirectToPage("/Messages/Thread", new { conversationId, restoreScrollTop = RestoreScrollTop });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            CanUseMessages = true;
            await LoadAsync(userId, conversationId, beforeMessageId: null, markRead: false, ct);
            if (isAjaxRequest)
            {
                return new BadRequestObjectResult(new { errors = GetModelStateErrors() });
            }

            return Page();
        }
    }

    public async Task<IActionResult> OnGetAttachment(long conversationId, long attachmentId, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Forbid();
        }

        if (!await _messages.IsEnabledAsync(ct))
        {
            return NotFound();
        }

        var attachment = await _messages.GetAttachmentAsync(userId, conversationId, attachmentId, ct);
        if (attachment is null)
        {
            return NotFound();
        }

        return File(attachment.Data, attachment.ContentType, attachment.FileName);
    }

    private async Task<bool> LoadAsync(
        int userId,
        long conversationId,
        long? beforeMessageId,
        bool markRead,
        CancellationToken ct)
    {
        Conversation = await _messages.GetConversationAsync(userId, conversationId, ct);
        if (Conversation is null)
        {
            return false;
        }

        IsHistoryView = beforeMessageId.HasValue;
        Rows = await _messages.GetMessagesAsync(userId, conversationId, 50, beforeMessageId, ct);
        HasMoreMessages = Rows.Count == 50;

        if (markRead)
        {
            await _messages.MarkConversationReadAsync(userId, conversationId, ct);
        }

        return true;
    }

    private bool TryGetCurrentUserId(out int userId)
    {
        var userIdClaim = User.FindFirstValue(OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(userIdClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId)
            && userId > 0;
    }

    private bool IsAjaxRequest()
        => string.Equals(Request.Headers["X-Requested-With"].ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    private string[] GetModelStateErrors()
        => ModelState.Values
            .SelectMany(entry => entry.Errors)
            .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                ? T("The message could not be sent.")
                : error.ErrorMessage)
            .ToArray();
}
