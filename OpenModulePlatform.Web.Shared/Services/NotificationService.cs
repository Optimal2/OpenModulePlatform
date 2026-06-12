using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Security;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Security.Claims;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Central service for user-facing OMP notifications.
/// </summary>
public sealed class NotificationService
{
    public const string MarkReadPath = "/notifications/mark-read";
    public const string MarkAllReadPath = "/notifications/mark-all-read";
    public const string RecentPath = "/notifications/recent";

    private static readonly HashSet<string> AllowedLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "info",
        "success",
        "warning",
        "error"
    };

    private readonly SqlConnectionFactory _db;

    public NotificationService(SqlConnectionFactory db)
    {
        _db = db;
    }

    public static int? TryGetOmpUserId(ClaimsPrincipal user)
    {
        var claimValue = user.FindFirstValue(OmpAuthDefaults.UserIdClaimType);
        return int.TryParse(claimValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            && userId > 0
            ? userId
            : null;
    }

    public async Task<long> CreateForUserAsync(
        int userId,
        NotificationCreateRequest request,
        CancellationToken ct)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId), "Personal notifications require an OMP user id greater than zero.");
        }

        ArgumentNullException.ThrowIfNull(request);

        var title = CleanRequired(request.Title, nameof(request.Title), 200);
        var content = CleanRequired(request.Content, nameof(request.Content), 1000);
        var level = NormalizeLevel(request.Level);
        var destinationUrl = CleanOptional(request.DestinationUrl, 600);
        if (!IsSafeInternalDestination(destinationUrl))
        {
            throw new ArgumentException("Notification destination must be a safe internal relative URL.", nameof(request));
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await NotificationsTableExistsAsync(conn, ct))
        {
            throw new InvalidOperationException("OMP notifications table is not installed.");
        }

        if (!await UserExistsAsync(conn, userId, ct))
        {
            throw new InvalidOperationException("The target OMP user does not exist.");
        }

        const string sql = @"
INSERT INTO omp.notifications
(
    user_id,
    title,
    content,
    destination_url,
    level,
    caller_key,
    caller_display_name,
    caller_icon,
    expires_at
)
OUTPUT INSERTED.notification_id
VALUES
(
    @user_id,
    @title,
    @content,
    @destination_url,
    @level,
    @caller_key,
    @caller_display_name,
    @caller_icon,
    @expires_at
);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@title", SqlDbType.NVarChar, 200).Value = title;
        cmd.Parameters.Add("@content", SqlDbType.NVarChar, 1000).Value = content;
        cmd.Parameters.Add("@destination_url", SqlDbType.NVarChar, 600).Value = ToDbValue(destinationUrl);
        cmd.Parameters.Add("@level", SqlDbType.NVarChar, 40).Value = level;
        cmd.Parameters.Add("@caller_key", SqlDbType.NVarChar, 200).Value = ToDbValue(CleanOptional(request.CallerKey, 200));
        cmd.Parameters.Add("@caller_display_name", SqlDbType.NVarChar, 200).Value = ToDbValue(CleanOptional(request.CallerDisplayName, 200));
        cmd.Parameters.Add("@caller_icon", SqlDbType.NVarChar, 600).Value = ToDbValue(CleanOptional(request.CallerIcon, 600));
        cmd.Parameters.Add("@expires_at", SqlDbType.DateTime2).Value = request.ExpiresAtUtc.HasValue
            ? request.ExpiresAtUtc.Value.UtcDateTime
            : (object)DBNull.Value;

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<PortalTopBarNotification>> GetRecentForUserAsync(
        int userId,
        int limit,
        DateTime? beforeCreatedAtUtc,
        long? beforeNotificationId,
        CancellationToken ct)
    {
        if (userId <= 0 || limit <= 0)
        {
            return Array.Empty<PortalTopBarNotification>();
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await NotificationsTableExistsAsync(conn, ct))
        {
            return Array.Empty<PortalTopBarNotification>();
        }

        const string sql = @"
SELECT TOP (@limit)
       notification_id,
       title,
       content,
       level,
       destination_url,
       caller_key,
       caller_display_name,
       caller_icon,
       created_at,
       CASE WHEN status = N'unread' AND read_at IS NULL THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS is_unread
FROM omp.notifications
WHERE user_id = @user_id
  AND level <> N'banner'
  AND (expires_at IS NULL OR expires_at > SYSUTCDATETIME())
  AND
  (
      @before_created_at IS NULL
      OR created_at < @before_created_at
      OR (created_at = @before_created_at AND notification_id < @before_notification_id)
  )
ORDER BY created_at DESC,
         notification_id DESC;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@before_created_at", SqlDbType.DateTime2).Value = beforeCreatedAtUtc.HasValue
            ? beforeCreatedAtUtc.Value
            : DBNull.Value;
        cmd.Parameters.Add("@before_notification_id", SqlDbType.BigInt).Value = beforeNotificationId.HasValue
            ? beforeNotificationId.Value
            : 0L;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<PortalTopBarNotification>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new PortalTopBarNotification(
                rdr.GetInt64(0),
                rdr.GetString(1),
                rdr.GetString(2),
                rdr.GetString(3),
                rdr.IsDBNull(4) ? null : rdr.GetString(4),
                rdr.IsDBNull(5) ? null : rdr.GetString(5),
                rdr.IsDBNull(6) ? null : rdr.GetString(6),
                rdr.IsDBNull(7) ? null : rdr.GetString(7),
                rdr.GetDateTime(8),
                rdr.GetBoolean(9)));
        }

        return rows;
    }

    public Task<IReadOnlyList<PortalTopBarNotification>> GetRecentForUserAsync(
        int userId,
        int limit,
        CancellationToken ct)
        => GetRecentForUserAsync(userId, limit, beforeCreatedAtUtc: null, beforeNotificationId: null, ct);

    public async Task<int> GetUnreadCountAsync(int userId, CancellationToken ct)
    {
        if (userId <= 0)
        {
            return 0;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await NotificationsTableExistsAsync(conn, ct))
        {
            return 0;
        }

        return await GetUnreadCountAsync(conn, userId, ct);
    }

    public async Task<NotificationMarkReadResult> MarkAsReadAsync(
        int userId,
        long notificationId,
        CancellationToken ct)
    {
        if (userId <= 0 || notificationId <= 0)
        {
            return new NotificationMarkReadResult(false, null, 0);
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await NotificationsTableExistsAsync(conn, ct))
        {
            return new NotificationMarkReadResult(false, null, 0);
        }

        string? destinationUrl = null;
        const string selectSql = @"
SELECT destination_url
FROM omp.notifications
WHERE notification_id = @notification_id
  AND user_id = @user_id
  AND level <> N'banner'
  AND (expires_at IS NULL OR expires_at > SYSUTCDATETIME());";

        await using (var selectCmd = new SqlCommand(selectSql, conn))
        {
            selectCmd.Parameters.Add("@notification_id", SqlDbType.BigInt).Value = notificationId;
            selectCmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
            await using var rdr = await selectCmd.ExecuteReaderAsync(ct);
            if (!await rdr.ReadAsync(ct))
            {
                return new NotificationMarkReadResult(false, null, await GetUnreadCountAsync(conn, userId, ct));
            }

            destinationUrl = rdr.IsDBNull(0) ? null : rdr.GetString(0);
        }

        const string updateSql = @"
UPDATE omp.notifications
SET status = N'read',
    read_at = COALESCE(read_at, SYSUTCDATETIME())
WHERE notification_id = @notification_id
  AND user_id = @user_id
  AND (status <> N'read' OR read_at IS NULL);";

        await using (var updateCmd = new SqlCommand(updateSql, conn))
        {
            updateCmd.Parameters.Add("@notification_id", SqlDbType.BigInt).Value = notificationId;
            updateCmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
            await updateCmd.ExecuteNonQueryAsync(ct);
        }

        return new NotificationMarkReadResult(true, destinationUrl, await GetUnreadCountAsync(conn, userId, ct));
    }

    public async Task<int> MarkAllAsReadAsync(int userId, CancellationToken ct)
    {
        if (userId <= 0)
        {
            return 0;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await NotificationsTableExistsAsync(conn, ct))
        {
            return 0;
        }

        const string sql = @"
UPDATE omp.notifications
SET status = N'read',
    read_at = COALESCE(read_at, SYSUTCDATETIME())
WHERE user_id = @user_id
  AND (status <> N'read' OR read_at IS NULL)
  AND level <> N'banner'
  AND (expires_at IS NULL OR expires_at > SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<NotificationToastSummary> GetToastSummaryForUserAsync(
        int userId,
        long? afterNotificationId,
        int limit,
        CancellationToken ct)
    {
        if (userId <= 0)
        {
            return new NotificationToastSummary(0, 0, 0, Array.Empty<NotificationToastItem>());
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await NotificationsTableExistsAsync(conn, ct))
        {
            return new NotificationToastSummary(0, 0, 0, Array.Empty<NotificationToastItem>());
        }

        var unreadCount = await GetUnreadCountAsync(conn, userId, ct);
        var latestNotificationId = await GetLatestNotificationIdAsync(conn, userId, ct);

        if (!afterNotificationId.HasValue)
        {
            return new NotificationToastSummary(unreadCount, latestNotificationId, 0, Array.Empty<NotificationToastItem>());
        }

        var baselineNotificationId = Math.Max(0L, afterNotificationId.Value);
        if (latestNotificationId <= baselineNotificationId)
        {
            return new NotificationToastSummary(unreadCount, latestNotificationId, 0, Array.Empty<NotificationToastItem>());
        }

        var pageSize = Math.Clamp(limit, 1, 20);
        var newNotificationCount = await GetNewNotificationCountAsync(conn, userId, baselineNotificationId, ct);
        const string sql = @"
SELECT TOP (@limit)
       notification_id,
       title,
       content,
       destination_url
FROM omp.notifications
WHERE user_id = @user_id
  AND notification_id > @after_notification_id
  AND level <> N'banner'
  AND (expires_at IS NULL OR expires_at > SYSUTCDATETIME())
ORDER BY notification_id DESC;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@limit", SqlDbType.Int).Value = pageSize;
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@after_notification_id", SqlDbType.BigInt).Value = baselineNotificationId;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<NotificationToastItem>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new NotificationToastItem(
                rdr.GetInt64(0),
                rdr.GetString(1),
                rdr.GetString(2),
                rdr.IsDBNull(3) ? null : rdr.GetString(3)));
        }

        return new NotificationToastSummary(unreadCount, latestNotificationId, newNotificationCount, rows);
    }

    private static async Task<long> GetLatestNotificationIdAsync(SqlConnection conn, int userId, CancellationToken ct)
    {
        const string sql = @"
SELECT ISNULL(MAX(notification_id), 0)
FROM omp.notifications
WHERE user_id = @user_id
  AND level <> N'banner'
  AND (expires_at IS NULL OR expires_at > SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<int> GetUnreadCountAsync(SqlConnection conn, int userId, CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT_BIG(1)
FROM omp.notifications
WHERE user_id = @user_id
  AND status = N'unread'
  AND read_at IS NULL
  AND level <> N'banner'
  AND (expires_at IS NULL OR expires_at > SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<int> GetNewNotificationCountAsync(
        SqlConnection conn,
        int userId,
        long afterNotificationId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT COUNT_BIG(1)
FROM omp.notifications
WHERE user_id = @user_id
  AND notification_id > @after_notification_id
  AND level <> N'banner'
  AND (expires_at IS NULL OR expires_at > SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        cmd.Parameters.Add("@after_notification_id", SqlDbType.BigInt).Value = afterNotificationId;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> NotificationsTableExistsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = "SELECT CAST(CASE WHEN OBJECT_ID(N'omp.notifications', N'U') IS NULL THEN 0 ELSE 1 END AS bit);";
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> UserExistsAsync(SqlConnection conn, int userId, CancellationToken ct)
    {
        const string sql = "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM omp.users WHERE user_id = @user_id) THEN 1 ELSE 0 END AS bit);";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@user_id", SqlDbType.Int).Value = userId;
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static string CleanRequired(string? value, string parameterName, int maxLength)
    {
        var cleaned = CleanOptional(value, maxLength);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        return cleaned;
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

    private static string NormalizeLevel(string? level)
    {
        var normalized = string.IsNullOrWhiteSpace(level)
            ? "info"
            : level.Trim().ToLowerInvariant();

        return AllowedLevels.Contains(normalized) ? normalized : "info";
    }

    private static object ToDbValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static bool IsSafeInternalDestination(string? destinationUrl)
    {
        if (string.IsNullOrWhiteSpace(destinationUrl))
        {
            return true;
        }

        if (!Uri.IsWellFormedUriString(destinationUrl, UriKind.Relative)
            || !destinationUrl.StartsWith("/", StringComparison.Ordinal)
            || destinationUrl.StartsWith("//", StringComparison.Ordinal)
            || destinationUrl.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var unescaped = Uri.UnescapeDataString(destinationUrl);
            return !unescaped.StartsWith("//", StringComparison.Ordinal)
                && !unescaped.Contains('\\', StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }
}

public sealed record NotificationCreateRequest(
    string? Title,
    string? Content,
    string? DestinationUrl = null,
    string? Level = "info",
    string? CallerKey = null,
    string? CallerDisplayName = null,
    string? CallerIcon = null,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record NotificationMarkReadResult(
    bool Success,
    string? DestinationUrl,
    int UnreadCount);

public sealed record NotificationToastSummary(
    int UnreadCount,
    long LatestNotificationId,
    int NewNotificationCount,
    IReadOnlyList<NotificationToastItem> NewNotifications);

public sealed record NotificationToastItem(
    long NotificationId,
    string Title,
    string Content,
    string? DestinationUrl);
