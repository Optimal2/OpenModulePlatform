using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Web.Shared.ActivityLog;
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
    private readonly ActivityLogWriter _activityLog;

    public ThreadModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        MessageService messages,
        ActivityLogWriter activityLog)
        : base(options, rbac)
    {
        _messages = messages;
        _activityLog = activityLog;
    }

    public int CurrentUserId { get; private set; }

    /// <summary>The signed-in user created this group and may remove other participants.</summary>
    public bool IsGroupAdmin => Conversation is { IsGroup: true } && Conversation.CreatedByUserId == CurrentUserId;

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
            ModelState.AddModelError(string.Empty, PortalTextLocalizer.Display(Localizer, ex.Message));
            CanUseMessages = true;
            await LoadAsync(userId, conversationId, beforeMessageId: null, markRead: false, ct);
            if (isAjaxRequest)
            {
                return new BadRequestObjectResult(new { errors = GetModelStateErrors() });
            }

            return Page();
        }
    }

    /// <summary>
    /// Rewrites one of the caller's own messages. The composer posts here in edit
    /// mode with the new text; the answer is the refreshed thread, like a send.
    /// </summary>
    public async Task<IActionResult> OnPostEdit(long conversationId, long messageId, CancellationToken ct)
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
            await _messages.EditMessageAsync(userId, messageId, MessageContent, ct);
            // The user log records that a message was changed, never its text.
            await _activityLog.WriteAsync(new ActivityEntry
            {
                Event = "message.edited",
                MessageKey = "message.edited",
                Summary = $"Edited message {messageId} in conversation {conversationId}",
                Subject = new ActivitySubject("message", messageId.ToString(CultureInfo.InvariantCulture)),
                Data = new Dictionary<string, object?> { ["conversationId"] = conversationId },
                Args = new Dictionary<string, object?> { ["messageId"] = messageId, ["conversationId"] = conversationId }
            }, User, ct);

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
            ModelState.AddModelError(string.Empty, PortalTextLocalizer.Display(Localizer, ex.Message));
            CanUseMessages = true;
            await LoadAsync(userId, conversationId, beforeMessageId: null, markRead: false, ct);
            if (isAjaxRequest)
            {
                return new BadRequestObjectResult(new { errors = GetModelStateErrors() });
            }

            return Page();
        }
    }

    /// <summary>The group's admin takes another participant out of the group.</summary>
    public async Task<IActionResult> OnPostRemoveParticipant(long conversationId, int userId, CancellationToken ct)
    {
        if (!TryGetCurrentUserId(out var actorUserId))
        {
            return Forbid();
        }

        if (!await _messages.IsEnabledAsync(ct) || userId == actorUserId)
        {
            return NotFound();
        }

        try
        {
            var removal = await _messages.RemoveParticipantAsync(actorUserId, conversationId, userId, ct);
            await WriteMembershipChangedAsync(removal, ct);
            StatusMessage = string.Format(CultureInfo.CurrentCulture, T("{0} was removed from the group."), removal.RemovedDisplayName);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = PortalTextLocalizer.Display(Localizer, ex.Message);
        }

        return RedirectToPage("/Messages/Thread", new { conversationId });
    }

    /// <summary>The signed-in user leaves the group; the creator's seat passes to the earliest joiner.</summary>
    public async Task<IActionResult> OnPostLeave(long conversationId, CancellationToken ct)
    {
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
            var removal = await _messages.RemoveParticipantAsync(userId, conversationId, userId, ct);
            await WriteMembershipChangedAsync(removal, ct);
            StatusMessage = T("You left the group.");
            return RedirectToPage("/Messages/Index");
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = PortalTextLocalizer.Display(Localizer, ex.Message);
            return RedirectToPage("/Messages/Thread", new { conversationId });
        }
    }

    private async Task WriteMembershipChangedAsync(MessageParticipantRemoval removal, CancellationToken ct)
    {
        var conversationId = removal.ConversationId.ToString(CultureInfo.InvariantCulture);
        await _activityLog.WriteAsync(new ActivityEntry
        {
            Event = removal.WasLeaving ? "conversation.left" : "conversation.participant_removed",
            MessageKey = removal.WasLeaving ? "conversation.left" : "conversation.participant_removed",
            Summary = removal.WasLeaving
                ? $"Left group conversation {conversationId}"
                : $"Removed {removal.RemovedDisplayName} (#{removal.RemovedUserId}) from group conversation {conversationId}",
            Subject = new ActivitySubject("conversation", conversationId),
            Data = new Dictionary<string, object?> { ["removedUserId"] = removal.RemovedUserId, ["newAdminUserId"] = removal.NewAdminUserId },
            Args = removal.WasLeaving
                ? new Dictionary<string, object?> { ["conversationId"] = removal.ConversationId }
                : new Dictionary<string, object?> { ["name"] = removal.RemovedDisplayName, ["userId"] = removal.RemovedUserId, ["conversationId"] = removal.ConversationId }
        }, User, ct);

        if (removal.NewAdminUserId is int newAdminUserId)
        {
            await _activityLog.WriteAsync(new ActivityEntry
            {
                Event = "conversation.admin_transferred",
                MessageKey = "conversation.admin_transferred",
                Summary = $"Group conversation {conversationId} is now administered by {removal.NewAdminDisplayName} (#{newAdminUserId})",
                Subject = new ActivitySubject("conversation", conversationId),
                Data = new Dictionary<string, object?> { ["newAdminUserId"] = newAdminUserId },
                Args = new Dictionary<string, object?> { ["conversationId"] = removal.ConversationId, ["name"] = removal.NewAdminDisplayName, ["userId"] = newAdminUserId }
            }, User, ct);
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
        CurrentUserId = userId;
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
