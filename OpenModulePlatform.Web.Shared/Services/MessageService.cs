using OpenModulePlatform.Web.Shared.Notifications;
using OpenModulePlatform.Web.Shared.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Text;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Central service for OMP user-to-user conversations.
/// </summary>
public sealed class MessageService
{
    // omp.users.account_status uses 1 for active accounts in the shared auth schema.
    private const int ActiveAccountStatus = 1;
    private const int MaxAttachmentCount = 5;
    private const long DefaultMaxAttachmentBytes = 5 * 1024 * 1024;
    private const long MinConfiguredAttachmentBytes = 1024;
    private const long MaxConfiguredAttachmentBytes = 100 * 1024 * 1024;
    private const int MaxFileNameLength = 260;
    private const int MaxPreviewLength = 160;
    private const int MaxMessageBatchSize = 100;
    private const string AttachmentHandlerName = "Attachment";
    private const string PreviewTruncationSuffix = "...";
    public const string ConfigurationCategory = "messages";
    public const string EnabledSetting = "enabled";
    public const string AttachmentMaxBytesSetting = "attachmentMaxBytes";
    public const string MarkAllReadPath = "/messages/mark-all-read";

    private static readonly HashSet<string> AllowedAttachmentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/zip",
        "text/plain"
    };

    private readonly SqlConnectionFactory _db;
    private readonly OmpConfigurationService _configuration;
    private readonly ITopBarNotificationStatePublisher _statePublisher;

    public MessageService(
        SqlConnectionFactory db,
        OmpConfigurationService configuration,
        ITopBarNotificationStatePublisher statePublisher)
    {
        _db = db;
        _configuration = configuration;
        _statePublisher = statePublisher;
    }

    // Keep a message-service facade so message endpoints do not need to depend
    // directly on notification service identity helpers.
    public static int? TryGetOmpUserId(ClaimsPrincipal user)
        => NotificationService.TryGetOmpUserId(user);

    public async Task<bool> IsInstalledAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);
        return await MessagesTablesExistAsync(conn, ct);
    }

    public async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        var value = await _configuration.GetGlobalStringAsync(
            ConfigurationCategory,
            EnabledSetting,
            ct);

        return ParseEnabled(value);
    }

    // Messages are opt-out: missing or invalid configuration keeps the feature
    // enabled for existing installations until an administrator disables it.
    public static bool ParseEnabled(string? value)
        => !bool.TryParse(value, out var enabled) || enabled;

    public async Task<int> GetUnreadMessageCountAsync(int userId, CancellationToken ct)
    {
        if (userId <= 0 || !await IsEnabledAsync(ct))
        {
            return 0;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await MessagesTablesExistAsync(conn, ct))
        {
            return 0;
        }

        const string sql = @"
SELECT COUNT_BIG(1)
FROM omp.conversation_participants cp
INNER JOIN omp.messages m ON m.conversation_id = cp.conversation_id
WHERE cp.user_id = @user_id
  AND cp.left_at IS NULL
  AND m.deleted_at IS NULL
  AND m.sender_user_id <> @user_id
  AND m.message_id > ISNULL(cp.last_read_message_id, 0);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<MessageToastSummary> GetToastSummaryForUserAsync(
        int userId,
        long? afterMessageId,
        int limit,
        CancellationToken ct)
    {
        if (userId <= 0 || !await IsEnabledAsync(ct))
        {
            return new MessageToastSummary(0, 0, 0, []);
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await MessagesTablesExistAsync(conn, ct))
        {
            return new MessageToastSummary(0, 0, 0, []);
        }

        var unreadCount = await GetUnreadMessageCountAsync(conn, userId, ct);
        var latestMessageId = await GetLatestUnreadMessageIdAsync(conn, userId, ct);

        if (!afterMessageId.HasValue)
        {
            return new MessageToastSummary(unreadCount, latestMessageId, 0, []);
        }

        var baselineMessageId = Math.Max(0L, afterMessageId.Value);
        if (latestMessageId <= baselineMessageId)
        {
            return new MessageToastSummary(unreadCount, latestMessageId, 0, []);
        }

        var pageSize = Math.Clamp(limit, 1, 20);
        var newMessageCount = await GetNewUnreadMessageCountAsync(conn, userId, baselineMessageId, ct);
        const string sql = @"
SELECT TOP (@limit)
       m.message_id,
       m.conversation_id,
       c.conversation_type,
       c.title,
       sender.display_name,
       m.content,
       other_user.display_name,
       participants.participant_names
FROM omp.conversation_participants cp
INNER JOIN omp.messages m ON m.conversation_id = cp.conversation_id
INNER JOIN omp.conversations c ON c.conversation_id = cp.conversation_id
INNER JOIN omp.users sender ON sender.user_id = m.sender_user_id
OUTER APPLY
(
    SELECT TOP (1)
           u.display_name
    FROM omp.conversation_participants cp2
    INNER JOIN omp.users u ON u.user_id = cp2.user_id
    WHERE cp2.conversation_id = c.conversation_id
      AND cp2.user_id <> @user_id
      AND cp2.left_at IS NULL
    ORDER BY u.display_name,
             u.user_id
) other_user
OUTER APPLY
(
    SELECT STRING_AGG(CONVERT(nvarchar(max), u.display_name), N', ') AS participant_names
    FROM omp.conversation_participants cp3
    INNER JOIN omp.users u ON u.user_id = cp3.user_id
    WHERE cp3.conversation_id = c.conversation_id
      AND cp3.left_at IS NULL
) participants
WHERE cp.user_id = @user_id
  AND cp.left_at IS NULL
  AND m.deleted_at IS NULL
  AND m.sender_user_id <> @user_id
  AND m.message_id > ISNULL(cp.last_read_message_id, 0)
  AND m.message_id > @after_message_id
ORDER BY m.message_id DESC;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@limit", SqlDbType.Int).Value = pageSize;
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@after_message_id", SqlDbType.BigInt).Value = baselineMessageId;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<MessageToastItem>();
        while (await rdr.ReadAsync(ct))
        {
            var conversationType = rdr.GetString(2);
            var title = rdr.IsDBNull(3) ? null : rdr.GetString(3);
            var senderDisplayName = rdr.GetString(4);
            var content = rdr.IsDBNull(5) ? null : rdr.GetString(5);
            var otherDisplayName = rdr.IsDBNull(6) ? null : rdr.GetString(6);
            var participantNames = rdr.IsDBNull(7) ? null : rdr.GetString(7);

            rows.Add(new MessageToastItem(
                rdr.GetInt64(0),
                rdr.GetInt64(1),
                BuildConversationTitle(conversationType, title, otherDisplayName, participantNames),
                BuildLastMessagePreview(senderDisplayName, content) ?? senderDisplayName));
        }

        return new MessageToastSummary(unreadCount, latestMessageId, newMessageCount, rows);
    }

    public async Task<IReadOnlyList<MessageConversationSummary>> GetConversationsForUserAsync(
        int userId,
        CancellationToken ct,
        int? limit = null)
    {
        if (userId <= 0 || !await IsEnabledAsync(ct))
        {
            return [];
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await MessagesTablesExistAsync(conn, ct))
        {
            return [];
        }

        const string sql = @"
SELECT TOP (@limit)
       c.conversation_id,
       c.conversation_type,
       c.title,
       c.last_message_at,
       last_message.content,
       last_sender.display_name,
       other_user.user_id,
       other_user.display_name,
       other_user.profile_image_storage_key,
       participants.participant_names,
       unread.unread_count
FROM omp.conversation_participants cp
INNER JOIN omp.conversations c ON c.conversation_id = cp.conversation_id
OUTER APPLY
(
    SELECT TOP (1)
           m.content,
           m.sender_user_id
    FROM omp.messages m
    WHERE m.conversation_id = c.conversation_id
      AND m.deleted_at IS NULL
    ORDER BY m.message_id DESC
) last_message
LEFT JOIN omp.users last_sender ON last_sender.user_id = last_message.sender_user_id
OUTER APPLY
(
    SELECT TOP (1)
           u.user_id,
           u.display_name,
           u.profile_image_storage_key
    FROM omp.conversation_participants cp2
    INNER JOIN omp.users u ON u.user_id = cp2.user_id
    WHERE cp2.conversation_id = c.conversation_id
      AND cp2.user_id <> @user_id
      AND cp2.left_at IS NULL
    ORDER BY u.display_name,
             u.user_id
) other_user
OUTER APPLY
(
    SELECT STRING_AGG(CONVERT(nvarchar(max), u.display_name), N', ') AS participant_names
    FROM omp.conversation_participants cp3
    INNER JOIN omp.users u ON u.user_id = cp3.user_id
    WHERE cp3.conversation_id = c.conversation_id
      AND cp3.left_at IS NULL
) participants
OUTER APPLY
(
    SELECT COUNT_BIG(1) AS unread_count
    FROM omp.messages m
    WHERE m.conversation_id = c.conversation_id
      AND m.deleted_at IS NULL
      AND m.sender_user_id <> @user_id
      AND m.message_id > ISNULL(cp.last_read_message_id, 0)
) unread
WHERE cp.user_id = @user_id
  AND cp.left_at IS NULL
ORDER BY COALESCE(c.last_message_at, c.updated_at, c.created_at) DESC,
         c.conversation_id DESC;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@limit", SqlDbType.Int).Value = limit.HasValue
            ? Math.Clamp(limit.Value, 1, 100)
            : int.MaxValue;
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;

        var rows = new List<MessageConversationSummary>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var conversationType = rdr.GetString(1);
            var title = rdr.IsDBNull(2) ? null : rdr.GetString(2);
            var otherUserId = rdr.IsDBNull(6) ? (int?)null : rdr.GetInt32(6);
            var otherDisplayName = rdr.IsDBNull(7) ? null : rdr.GetString(7);
            var otherProfileImageStorageKey = rdr.IsDBNull(8) ? null : rdr.GetString(8);
            var participantNames = rdr.IsDBNull(9) ? null : rdr.GetString(9);
            var displayTitle = BuildConversationTitle(conversationType, title, otherDisplayName, participantNames);
            var senderDisplayName = rdr.IsDBNull(5) ? null : rdr.GetString(5);
            var content = rdr.IsDBNull(4) ? null : rdr.GetString(4);

            rows.Add(new MessageConversationSummary(
                rdr.GetInt64(0),
                conversationType,
                title,
                otherUserId,
                otherDisplayName,
                otherProfileImageStorageKey,
                displayTitle,
                BuildLastMessagePreview(senderDisplayName, content),
                rdr.IsDBNull(3) ? null : rdr.GetDateTime(3),
                Convert.ToInt32(rdr.GetInt64(10), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    public async Task<MessageConversationDetail?> GetConversationAsync(
        int userId,
        long conversationId,
        CancellationToken ct)
    {
        if (userId <= 0 || conversationId <= 0 || !await IsEnabledAsync(ct))
        {
            return null;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await MessagesTablesExistAsync(conn, ct)
            || !await UserCanAccessConversationAsync(conn, tx: null, userId, conversationId, ct))
        {
            return null;
        }

        const string sql = @"
SELECT c.conversation_id,
       c.conversation_type,
       c.title,
       other_user.user_id,
       other_user.display_name,
       other_user.profile_image_storage_key,
       participants.participant_names
FROM omp.conversations c
OUTER APPLY
(
    SELECT TOP (1)
           u.user_id,
           u.display_name,
           u.profile_image_storage_key
    FROM omp.conversation_participants cp2
    INNER JOIN omp.users u ON u.user_id = cp2.user_id
    WHERE cp2.conversation_id = c.conversation_id
      AND cp2.user_id <> @user_id
      AND cp2.left_at IS NULL
    ORDER BY u.display_name,
             u.user_id
) other_user
OUTER APPLY
(
    SELECT STRING_AGG(CONVERT(nvarchar(max), u.display_name), N', ') AS participant_names
    FROM omp.conversation_participants cp3
    INNER JOIN omp.users u ON u.user_id = cp3.user_id
    WHERE cp3.conversation_id = c.conversation_id
      AND cp3.left_at IS NULL
) participants
WHERE c.conversation_id = @conversation_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@conversation_id", SqlDbType.BigInt).Value = conversationId;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        var conversationType = rdr.GetString(1);
        var title = rdr.IsDBNull(2) ? null : rdr.GetString(2);
        var otherUserId = rdr.IsDBNull(3) ? (int?)null : rdr.GetInt32(3);
        var otherDisplayName = rdr.IsDBNull(4) ? null : rdr.GetString(4);
        var otherProfileImageStorageKey = rdr.IsDBNull(5) ? null : rdr.GetString(5);
        var participantNames = rdr.IsDBNull(6) ? null : rdr.GetString(6);

        return new MessageConversationDetail(
            rdr.GetInt64(0),
            conversationType,
            title,
            otherUserId,
            otherDisplayName,
            otherProfileImageStorageKey,
            BuildConversationTitle(conversationType, title, otherDisplayName, participantNames),
            participantNames);
    }

    public async Task<long> GetOrCreateDirectConversationAsync(
        int userA,
        int userB,
        CancellationToken ct)
    {
        if (userA <= 0 || userB <= 0 || userA == userB)
        {
            throw new ArgumentException("Direct conversations require two distinct OMP users.");
        }

        await RequireEnabledAsync(ct);

        var low = Math.Min(userA, userB);
        var high = Math.Max(userA, userB);

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await MessagesTablesExistAsync(conn, ct))
        {
            throw new InvalidOperationException("OMP messages tables are not installed.");
        }

        var existing = await GetDirectConversationIdAsync(conn, tx: null, low, high, ct);
        if (existing is long existingId)
        {
            return existingId;
        }

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            existing = await GetDirectConversationIdAsync(conn, tx, low, high, ct);
            if (existing is long existingIdInTransaction)
            {
                await tx.CommitAsync(ct);
                return existingIdInTransaction;
            }

            await RequireActiveUserAsync(conn, tx, low, ct);
            await RequireActiveUserAsync(conn, tx, high, ct);

            var conversationId = await InsertConversationAsync(conn, tx, "direct", title: null, createdByUserId: userA, ct);
            await InsertParticipantAsync(conn, tx, conversationId, low, ct);
            await InsertParticipantAsync(conn, tx, conversationId, high, ct);

            const string directSql = @"
INSERT INTO omp.direct_conversations(user_low_id, user_high_id, conversation_id)
VALUES(@low, @high, @conversation_id);";
            await using (var cmd = new SqlCommand(directSql, conn, tx))
            {
                cmd.Parameters.Add("@low", SqlDbType.Int).Value = low;
                cmd.Parameters.Add("@high", SqlDbType.Int).Value = high;
                cmd.Parameters.Add("@conversation_id", SqlDbType.BigInt).Value = conversationId;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return conversationId;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            await tx.RollbackAsync(ct);
            if (await GetDirectConversationIdAsync(conn, tx: null, low, high, ct) is long retryId)
            {
                return retryId;
            }

            throw;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<long> CreateGroupConversationAsync(
        int createdByUserId,
        IEnumerable<int> participantUserIds,
        string? title,
        CancellationToken ct)
    {
        if (createdByUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(createdByUserId));
        }

        await RequireEnabledAsync(ct);

        var participants = participantUserIds
            .Append(createdByUserId)
            .Where(userId => userId > 0)
            .Distinct()
            .Order()
            .ToArray();

        if (participants.Length < 2)
        {
            throw new ArgumentException("Group conversations require at least two OMP users.", nameof(participantUserIds));
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await MessagesTablesExistAsync(conn, ct))
        {
            throw new InvalidOperationException("OMP messages tables are not installed.");
        }

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var participantUserId in participants)
            {
                await RequireActiveUserAsync(conn, tx, participantUserId, ct);
            }

            var conversationId = await InsertConversationAsync(
                conn,
                tx,
                "group",
                CleanOptional(title, 200),
                createdByUserId,
                ct);

            foreach (var participantUserId in participants)
            {
                await InsertParticipantAsync(conn, tx, conversationId, participantUserId, ct);
            }

            await tx.CommitAsync(ct);
            return conversationId;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<MessageRow>> GetMessagesAsync(
        int userId,
        long conversationId,
        int limit,
        long? beforeMessageId,
        CancellationToken ct)
    {
        if (userId <= 0 || conversationId <= 0 || limit <= 0 || !await IsEnabledAsync(ct))
        {
            return [];
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await MessagesTablesExistAsync(conn, ct)
            || !await UserCanAccessConversationAsync(conn, tx: null, userId, conversationId, ct))
        {
            return [];
        }

        const string sql = @"
SELECT TOP (@limit)
       m.message_id,
       m.sender_user_id,
       u.display_name,
       u.profile_image_storage_key,
       m.content,
       m.message_type,
       m.created_at
FROM omp.messages m
INNER JOIN omp.users u ON u.user_id = m.sender_user_id
WHERE m.conversation_id = @conversation_id
  AND m.deleted_at IS NULL
  AND (@before_message_id IS NULL OR m.message_id < @before_message_id)
ORDER BY m.message_id DESC;";

        var rows = new List<MessageRow>();
        await using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.Add("@limit", SqlDbType.Int).Value = Math.Clamp(limit, 1, MaxMessageBatchSize);
            cmd.Parameters.Add("@conversation_id", SqlDbType.BigInt).Value = conversationId;
            cmd.Parameters.Add("@before_message_id", SqlDbType.BigInt).Value = beforeMessageId.HasValue
                ? beforeMessageId.Value
                : DBNull.Value;

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                rows.Add(new MessageRow(
                    rdr.GetInt64(0),
                    conversationId,
                    rdr.GetInt32(1),
                    rdr.GetString(2),
                    rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    rdr.GetString(5),
                    rdr.GetDateTime(6),
                    rdr.GetInt32(1) == userId,
                    []));
            }
        }

        rows.Reverse();
        var attachmentsByMessageId = await GetAttachmentsForMessagesAsync(
            conn,
            conversationId,
            rows.Select(row => row.MessageId).ToArray(),
            ct);

        for (var i = 0; i < rows.Count; i++)
        {
            var attachments = attachmentsByMessageId.TryGetValue(rows[i].MessageId, out var messageAttachments)
                ? messageAttachments
                : [];

            rows[i] = rows[i] with
            {
                Attachments = attachments
            };
        }

        return rows;
    }

    public async Task<long> SendMessageAsync(
        int userId,
        long conversationId,
        string? content,
        IReadOnlyList<IFormFile> attachments,
        CancellationToken ct)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }
        if (conversationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(conversationId));
        }

        await RequireEnabledAsync(ct);

        var cleanedContent = CleanOptional(content, maxLength: 4000);
        var normalizedAttachments = attachments
            .Where(file => file.Length > 0)
            .Take(MaxAttachmentCount + 1)
            .ToArray();

        if (string.IsNullOrWhiteSpace(cleanedContent) && normalizedAttachments.Length == 0)
        {
            throw new ArgumentException("A message must contain text or at least one attachment.", nameof(content));
        }

        if (normalizedAttachments.Length > MaxAttachmentCount)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A message can include at most {MaxAttachmentCount} attachments."));
        }

        var maxAttachmentBytes = await GetMaxAttachmentBytesAsync(ct);
        foreach (var file in normalizedAttachments)
        {
            ValidateAttachment(file, maxAttachmentBytes);
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await MessagesTablesExistAsync(conn, ct))
        {
            throw new InvalidOperationException("OMP messages tables are not installed.");
        }

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        long messageId;
        IReadOnlyList<int> recipientUserIds;
        try
        {
            if (!await UserCanAccessConversationAsync(conn, tx, userId, conversationId, ct))
            {
                throw new UnauthorizedAccessException("Only conversation participants can send messages.");
            }

            recipientUserIds = await GetConversationParticipantUserIdsAsync(conn, tx, conversationId, userId, ct);

            const string messageSql = @"
INSERT INTO omp.messages(conversation_id, sender_user_id, content, message_type)
OUTPUT INSERTED.message_id
VALUES(@conversation_id, @sender_user_id, @content, N'text');";

            await using (var cmd = new SqlCommand(messageSql, conn, tx))
            {
                cmd.Parameters.Add("@conversation_id", SqlDbType.BigInt).Value = conversationId;
                cmd.Parameters.Add("@sender_user_id", SqlDbType.Int).Value = userId;
                cmd.Parameters.Add("@content", SqlDbType.NVarChar, -1).Value = ToDbValue(cleanedContent);
                messageId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }

            foreach (var file in normalizedAttachments)
            {
                await InsertAttachmentAsync(conn, tx, messageId, userId, file, ct);
            }

            const string updateSql = @"
UPDATE omp.conversations
SET updated_at = SYSUTCDATETIME(),
    last_message_at = SYSUTCDATETIME()
WHERE conversation_id = @conversation_id;";

            await using (var cmd = new SqlCommand(updateSql, conn, tx))
            {
                cmd.Parameters.Add("@conversation_id", SqlDbType.BigInt).Value = conversationId;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        foreach (var recipientUserId in recipientUserIds)
        {
            await _statePublisher.NotifyChangedAsync(recipientUserId, ct);
        }

        return messageId;
    }

    public async Task MarkConversationReadAsync(int userId, long conversationId, CancellationToken ct)
    {
        if (userId <= 0 || conversationId <= 0 || !await IsEnabledAsync(ct))
        {
            return;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await MessagesTablesExistAsync(conn, ct))
        {
            return;
        }

        var unreadCount = await GetUnreadMessageCountAsync(conn, userId, conversationId, ct);

        const string sql = @"
UPDATE cp
SET last_read_message_id =
    (
        SELECT MAX(m.message_id)
        FROM omp.messages m
        WHERE m.conversation_id = cp.conversation_id
          AND m.deleted_at IS NULL
    )
FROM omp.conversation_participants cp
WHERE cp.conversation_id = @conversation_id
  AND cp.user_id = @user_id
  AND cp.left_at IS NULL;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@conversation_id", SqlDbType.BigInt).Value = conversationId;
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        await cmd.ExecuteNonQueryAsync(ct);

        if (unreadCount > 0)
        {
            await _statePublisher.NotifyChangedAsync(userId, ct);
        }
    }

    public async Task<int> MarkAllConversationsReadAsync(int userId, CancellationToken ct)
    {
        if (userId <= 0 || !await IsEnabledAsync(ct))
        {
            return 0;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await MessagesTablesExistAsync(conn, ct))
        {
            return 0;
        }

        // This count is intentionally captured before the update; callers use
        // it as the number of unread messages that were marked read.
        var messagesMarkedRead = await GetUnreadMessageCountAsync(conn, userId, ct);

        const string sql = @"
UPDATE cp
SET last_read_message_id = latest.latest_message_id
FROM omp.conversation_participants cp
OUTER APPLY
(
    SELECT MAX(m.message_id) AS latest_message_id
    FROM omp.messages m
    WHERE m.conversation_id = cp.conversation_id
      AND m.deleted_at IS NULL
) latest
WHERE cp.user_id = @user_id
  AND cp.left_at IS NULL
  AND latest.latest_message_id IS NOT NULL
  AND ISNULL(cp.last_read_message_id, 0) < latest.latest_message_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        await cmd.ExecuteNonQueryAsync(ct);
        if (messagesMarkedRead > 0)
        {
            await _statePublisher.NotifyChangedAsync(userId, ct);
        }

        return messagesMarkedRead;
    }

    private static async Task<IReadOnlyList<int>> GetConversationParticipantUserIdsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        long conversationId,
        int excludedUserId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT user_id
FROM omp.conversation_participants
WHERE conversation_id = @conversation_id
  AND left_at IS NULL
  AND user_id <> @excluded_user_id
ORDER BY user_id;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@conversation_id", SqlDbType.BigInt).Value = conversationId;
        cmd.Parameters.Add("@excluded_user_id", SqlDbType.Int).Value = excludedUserId;

        var userIds = new List<int>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            userIds.Add(rdr.GetInt32(0));
        }

        return userIds;
    }

    private static async Task<int> GetUnreadMessageCountAsync(SqlConnection conn, int userId, CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT_BIG(1)
FROM omp.conversation_participants cp
INNER JOIN omp.messages m ON m.conversation_id = cp.conversation_id
WHERE cp.user_id = @user_id
  AND cp.left_at IS NULL
  AND m.deleted_at IS NULL
  AND m.sender_user_id <> @user_id
  AND m.message_id > ISNULL(cp.last_read_message_id, 0);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<int> GetUnreadMessageCountAsync(
        SqlConnection conn,
        int userId,
        long conversationId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT_BIG(1)
FROM omp.conversation_participants cp
INNER JOIN omp.messages m ON m.conversation_id = cp.conversation_id
WHERE cp.user_id = @user_id
  AND cp.conversation_id = @conversation_id
  AND cp.left_at IS NULL
  AND m.deleted_at IS NULL
  AND m.sender_user_id <> @user_id
  AND m.message_id > ISNULL(cp.last_read_message_id, 0);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@conversation_id", SqlDbType.BigInt).Value = conversationId;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<long> GetLatestUnreadMessageIdAsync(SqlConnection conn, int userId, CancellationToken ct)
    {
        const string sql = @"
SELECT ISNULL(MAX(m.message_id), 0)
FROM omp.conversation_participants cp
INNER JOIN omp.messages m ON m.conversation_id = cp.conversation_id
WHERE cp.user_id = @user_id
  AND cp.left_at IS NULL
  AND m.deleted_at IS NULL
  AND m.sender_user_id <> @user_id
  AND m.message_id > ISNULL(cp.last_read_message_id, 0);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<int> GetNewUnreadMessageCountAsync(
        SqlConnection conn,
        int userId,
        long afterMessageId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT_BIG(1)
FROM omp.conversation_participants cp
INNER JOIN omp.messages m ON m.conversation_id = cp.conversation_id
WHERE cp.user_id = @user_id
  AND cp.left_at IS NULL
  AND m.deleted_at IS NULL
  AND m.sender_user_id <> @user_id
  AND m.message_id > ISNULL(cp.last_read_message_id, 0)
  AND m.message_id > @after_message_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@after_message_id", SqlDbType.BigInt).Value = afterMessageId;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<MessageUserOption>> SearchUsersAsync(
        int currentUserId,
        string? query,
        int limit,
        CancellationToken ct)
    {
        if (currentUserId <= 0 || !await IsEnabledAsync(ct))
        {
            return [];
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT TOP (@limit)
       user_id,
       display_name
FROM omp.users
WHERE user_id > 0
  AND account_status = @active_status
  AND user_id <> @current_user_id
  AND
  (
      @query IS NULL
      OR display_name LIKE @like_query
      OR CONVERT(nvarchar(20), user_id) = @query
  )
ORDER BY display_name,
         user_id;";

        var cleanedQuery = CleanOptional(query, 100);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@limit", SqlDbType.Int).Value = Math.Clamp(limit, 1, 100);
        cmd.Parameters.Add("@active_status", SqlDbType.Int).Value = ActiveAccountStatus;
        cmd.Parameters.Add("@current_user_id", SqlDbType.Int).Value = currentUserId;
        cmd.Parameters.Add("@query", SqlDbType.NVarChar, 100).Value = ToDbValue(cleanedQuery);
        cmd.Parameters.Add("@like_query", SqlDbType.NVarChar, 104).Value = string.IsNullOrWhiteSpace(cleanedQuery)
            ? DBNull.Value
            : $"%{cleanedQuery}%";

        var rows = new List<MessageUserOption>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new MessageUserOption(rdr.GetInt32(0), rdr.GetString(1)));
        }

        return rows;
    }

    public async Task<MessageAttachmentDownload?> GetAttachmentAsync(
        int userId,
        long conversationId,
        long attachmentId,
        CancellationToken ct)
    {
        if (userId <= 0 || conversationId <= 0 || attachmentId <= 0 || !await IsEnabledAsync(ct))
        {
            return null;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await MessagesTablesExistAsync(conn, ct)
            || !await UserCanAccessConversationAsync(conn, tx: null, userId, conversationId, ct))
        {
            return null;
        }

        const string sql = @"
SELECT a.file_name,
       a.content_type,
       a.data_value
FROM omp.message_attachments a
INNER JOIN omp.messages m ON m.message_id = a.message_id
WHERE a.attachment_id = @attachment_id
  AND m.conversation_id = @conversation_id
  AND m.deleted_at IS NULL;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@attachment_id", SqlDbType.BigInt).Value = attachmentId;
        cmd.Parameters.Add("@conversation_id", SqlDbType.BigInt).Value = conversationId;

        await using var rdr = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        return new MessageAttachmentDownload(
            rdr.GetString(0),
            rdr.GetString(1),
            await rdr.GetFieldValueAsync<byte[]>(2, ct));
    }

    private static async Task<bool> MessagesTablesExistAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT CAST(CASE
    WHEN OBJECT_ID(N'omp.conversations', N'U') IS NOT NULL
     AND OBJECT_ID(N'omp.conversation_participants', N'U') IS NOT NULL
     AND OBJECT_ID(N'omp.messages', N'U') IS NOT NULL
     AND OBJECT_ID(N'omp.message_attachments', N'U') IS NOT NULL
     AND OBJECT_ID(N'omp.direct_conversations', N'U') IS NOT NULL
    THEN 1 ELSE 0 END AS bit);";

        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> UserCanAccessConversationAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        int userId,
        long conversationId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT CAST(CASE WHEN EXISTS
(
    SELECT 1
    FROM omp.conversation_participants
    WHERE conversation_id = @conversation_id
      AND user_id = @user_id
      AND left_at IS NULL
) THEN 1 ELSE 0 END AS bit);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@conversation_id", SqlDbType.BigInt).Value = conversationId;
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<long?> GetDirectConversationIdAsync(
        SqlConnection conn,
        SqlTransaction? tx,
        int low,
        int high,
        CancellationToken ct)
    {
        const string sql = @"
SELECT conversation_id
FROM omp.direct_conversations
WHERE user_low_id = @low
  AND user_high_id = @high;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@low", SqlDbType.Int).Value = low;
        cmd.Parameters.Add("@high", SqlDbType.Int).Value = high;
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null || value == DBNull.Value
            ? null
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task RequireActiveUserAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int userId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT CAST(CASE WHEN EXISTS
(
    SELECT 1
    FROM omp.users
    WHERE user_id = @user_id
      AND account_status = @active_status
) THEN 1 ELSE 0 END AS bit);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@active_status", SqlDbType.Int).Value = ActiveAccountStatus;

        if (!Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("OMP user does not exist or is not active.");
        }
    }

    private static async Task<long> InsertConversationAsync(
        SqlConnection conn,
        SqlTransaction tx,
        string conversationType,
        string? title,
        int createdByUserId,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.conversations(conversation_type, title, created_by_user_id)
OUTPUT INSERTED.conversation_id
VALUES(@conversation_type, @title, @created_by_user_id);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@conversation_type", SqlDbType.NVarChar, 40).Value = conversationType;
        cmd.Parameters.Add("@title", SqlDbType.NVarChar, 200).Value = ToDbValue(title);
        cmd.Parameters.Add("@created_by_user_id", SqlDbType.Int).Value = createdByUserId;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task InsertParticipantAsync(
        SqlConnection conn,
        SqlTransaction tx,
        long conversationId,
        int userId,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO omp.conversation_participants(conversation_id, user_id)
VALUES(@conversation_id, @user_id);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@conversation_id", SqlDbType.BigInt).Value = conversationId;
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertAttachmentAsync(
        SqlConnection conn,
        SqlTransaction tx,
        long messageId,
        int userId,
        IFormFile file,
        CancellationToken ct)
    {
        var storageKey = Guid.NewGuid().ToString("N");
        var fileName = CleanFileName(file.FileName);
        var contentType = CleanOptional(file.ContentType, 128) ?? "application/octet-stream";
        var initialCapacity = file.Length is > 0 and <= int.MaxValue
            ? (int)file.Length
            : 0;
        await using var ms = initialCapacity > 0
            ? new MemoryStream(initialCapacity)
            : new MemoryStream();
        await file.CopyToAsync(ms, ct);

        const string sql = @"
INSERT INTO omp.message_attachments
(
    message_id,
    file_name,
    content_type,
    file_size,
    storage_key,
    data_value,
    uploaded_by_user_id
)
VALUES
(
    @message_id,
    @file_name,
    @content_type,
    @file_size,
    @storage_key,
    @data_value,
    @uploaded_by_user_id
);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@message_id", SqlDbType.BigInt).Value = messageId;
        cmd.Parameters.Add("@file_name", SqlDbType.NVarChar, MaxFileNameLength).Value = fileName;
        cmd.Parameters.Add("@content_type", SqlDbType.NVarChar, 128).Value = contentType;
        cmd.Parameters.Add("@file_size", SqlDbType.BigInt).Value = file.Length;
        cmd.Parameters.Add("@storage_key", SqlDbType.NVarChar, 120).Value = storageKey;
        cmd.Parameters.Add("@data_value", SqlDbType.VarBinary, -1).Value = ms.ToArray();
        cmd.Parameters.Add("@uploaded_by_user_id", SqlDbType.Int).Value = userId;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyDictionary<long, IReadOnlyList<MessageAttachmentRow>>> GetAttachmentsForMessagesAsync(
        SqlConnection conn,
        long conversationId,
        IReadOnlyList<long> messageIds,
        CancellationToken ct)
    {
        if (messageIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<MessageAttachmentRow>>();
        }
        if (messageIds.Count > MaxMessageBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(messageIds),
                messageIds.Count,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Message attachment batches are limited to {MaxMessageBatchSize} messages."));
        }

        // The dynamic SQL below only contains generated parameter names for a bounded batch size.
        // All message ids remain SqlParameter values.
        var values = BuildMessageIdValuesClause(messageIds.Count);

        var sql = $@"
WITH RequestedMessages(message_id) AS
(
    SELECT v.message_id
    FROM (VALUES {values}) AS v(message_id)
)
SELECT a.message_id,
       a.attachment_id,
       a.file_name,
       a.content_type,
       a.file_size
FROM omp.message_attachments a
INNER JOIN RequestedMessages requested
    ON requested.message_id = a.message_id
ORDER BY a.message_id, a.attachment_id;";

        await using var cmd = new SqlCommand(sql, conn);
        for (var i = 0; i < messageIds.Count; i++)
        {
            cmd.Parameters.Add(GetMessageIdParameterName(i), SqlDbType.BigInt).Value = messageIds[i];
        }

        var rows = new Dictionary<long, List<MessageAttachmentRow>>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var messageId = rdr.GetInt64(0);
            var attachmentId = rdr.GetInt64(1);
            var contentType = rdr.GetString(3);
            if (!rows.TryGetValue(messageId, out var attachments))
            {
                attachments = [];
                rows[messageId] = attachments;
            }

            attachments.Add(new MessageAttachmentRow(
                attachmentId,
                rdr.GetString(2),
                contentType,
                rdr.GetInt64(4),
                BuildAttachmentDownloadUrl(conversationId, attachmentId),
                IsImageContentType(contentType)));
        }

        return rows.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MessageAttachmentRow>)pair.Value);
    }

    private static string BuildMessageIdValuesClause(int count)
    {
        var builder = new StringBuilder(count * 12);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append('(');
            builder.Append(GetMessageIdParameterName(i));
            builder.Append(')');
        }

        return builder.ToString();
    }

    private static string GetMessageIdParameterName(int index)
        => string.Create(CultureInfo.InvariantCulture, $"@message{index}");

    private static string BuildAttachmentDownloadUrl(long conversationId, long attachmentId)
        => string.Concat(
            "/messages/",
            conversationId.ToString(CultureInfo.InvariantCulture),
            "?handler=",
            AttachmentHandlerName,
            "&attachmentId=",
            attachmentId.ToString(CultureInfo.InvariantCulture));

    private async Task<long> GetMaxAttachmentBytesAsync(CancellationToken ct)
    {
        var value = await _configuration.GetGlobalStringAsync(
            ConfigurationCategory,
            AttachmentMaxBytesSetting,
            ct);

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var configuredBytes))
        {
            return DefaultMaxAttachmentBytes;
        }

        return Math.Clamp(configuredBytes, MinConfiguredAttachmentBytes, MaxConfiguredAttachmentBytes);
    }

    private async Task RequireEnabledAsync(CancellationToken ct)
    {
        if (!await IsEnabledAsync(ct))
        {
            throw new InvalidOperationException("OMP messages are disabled.");
        }
    }

    private static void ValidateAttachment(IFormFile file, long maxAttachmentBytes)
    {
        if (file.Length <= 0)
        {
            return;
        }

        if (file.Length > maxAttachmentBytes)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Attachment file is too large. The current limit is {FormatByteSize(maxAttachmentBytes)}."));
        }

        var contentType = CleanOptional(file.ContentType, 128) ?? "application/octet-stream";
        if (!IsImageContentType(contentType) && !AllowedAttachmentTypes.Contains(contentType))
        {
            throw new InvalidOperationException("Attachment content type is not allowed.");
        }
    }

    private static string FormatByteSize(long bytes)
    {
        var megabytes = bytes / 1024d / 1024d;
        var kilobytes = bytes / 1024d;
        return megabytes >= 1
            ? $"{megabytes.ToString("0.#", CultureInfo.InvariantCulture)} MB"
            : $"{Math.Max(1d, kilobytes).ToString("0.#", CultureInfo.InvariantCulture)} KB";
    }

    private static string BuildConversationTitle(
        string conversationType,
        string? title,
        string? otherDisplayName,
        string? participantNames)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title.Trim();
        }

        if (string.Equals(conversationType, "direct", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(otherDisplayName))
        {
            return otherDisplayName.Trim();
        }

        return string.IsNullOrWhiteSpace(participantNames)
            ? "Conversation"
            : participantNames.Trim();
    }

    private static string? BuildLastMessagePreview(string? senderDisplayName, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var cleaned = content.Trim();
        cleaned = TruncateTextElements(cleaned, MaxPreviewLength);

        return string.IsNullOrWhiteSpace(senderDisplayName)
            ? cleaned
            : $"{senderDisplayName}: {cleaned}";
    }

    private static string TruncateTextElements(string value, int maxTextElements)
    {
        if (maxTextElements <= 0 || string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var textElementIndexes = StringInfo.ParseCombiningCharacters(value);
        if (textElementIndexes.Length <= maxTextElements)
        {
            return value;
        }

        var suffixTextElements = StringInfo.ParseCombiningCharacters(PreviewTruncationSuffix).Length;
        var contentTextElements = Math.Max(1, maxTextElements - suffixTextElements);
        var endIndex = textElementIndexes[contentTextElements];
        return value[..endIndex] + PreviewTruncationSuffix;
    }

    private static string CleanFileName(string? value)
    {
        var fileName = Path.GetFileName(value);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "attachment";
        }

        fileName = fileName.Trim();
        if (fileName.Length <= MaxFileNameLength)
        {
            return fileName;
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension) || extension.Length >= MaxFileNameLength)
        {
            return fileName[..MaxFileNameLength];
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var maxNameLength = MaxFileNameLength - extension.Length;
        return nameWithoutExtension.Length <= maxNameLength
            ? nameWithoutExtension + extension
            : nameWithoutExtension[..maxNameLength] + extension;
    }

    private static string? CleanOptional(string? value, int maxLength)
    {
        var cleaned = value?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static bool IsImageContentType(string? contentType)
        => !string.IsNullOrWhiteSpace(contentType)
           && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static object ToDbValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}

public sealed record MessageConversationSummary(
    long ConversationId,
    string ConversationType,
    string? Title,
    int? OtherUserId,
    string? OtherDisplayName,
    string? OtherProfileImageStorageKey,
    string DisplayTitle,
    string? LastMessagePreview,
    DateTime? LastMessageAt,
    int UnreadCount);

public sealed record MessageConversationDetail(
    long ConversationId,
    string ConversationType,
    string? Title,
    int? OtherUserId,
    string? OtherDisplayName,
    string? OtherProfileImageStorageKey,
    string DisplayTitle,
    string? ParticipantNames);

public sealed record MessageRow(
    long MessageId,
    long ConversationId,
    int SenderUserId,
    string SenderDisplayName,
    string? SenderProfileImageStorageKey,
    string? Content,
    string MessageType,
    DateTime CreatedAt,
    bool IsOwnMessage,
    IReadOnlyList<MessageAttachmentRow> Attachments);

public sealed record MessageAttachmentRow(
    long AttachmentId,
    string FileName,
    string ContentType,
    long FileSize,
    string DownloadUrl,
    bool IsImage);

public sealed record MessageAttachmentDownload(
    string FileName,
    string ContentType,
    byte[] Data);

public sealed record MessageUserOption(
    int UserId,
    string DisplayName);

public sealed record MessageToastSummary(
    int UnreadCount,
    long LatestMessageId,
    int NewMessageCount,
    IReadOnlyList<MessageToastItem> NewMessages);

public sealed record MessageToastItem(
    long MessageId,
    long ConversationId,
    string Title,
    string Content);
