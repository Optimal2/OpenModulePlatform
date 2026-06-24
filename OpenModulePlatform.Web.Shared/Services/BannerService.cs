using Microsoft.Data.SqlClient;
using OpenModulePlatform.EventPublisher;
using OpenModulePlatform.Web.Shared.Navigation;
using System.Data;
using System.Globalization;
using System.Text.Json;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>
/// Central service for system-controlled OMP banners.
/// </summary>
public sealed class BannerService
{
    public const string StatusActive = "active";
    public const string StatusDisabled = "disabled";
    public const string TargetGlobal = "global";
    public const string TargetRole = "role";
    public const int LevelAnnouncement = 1;
    public const int LevelWarning = 2;
    public const int LevelCritical = 3;

    private const string InfoIconUrl = "/_content/OpenModulePlatform.Web.Shared/icons/info.svg";
    private const string WarningIconUrl = "/_content/OpenModulePlatform.Web.Shared/icons/warning.svg";

    private readonly SqlConnectionFactory _db;
    private readonly IPushEventPublisher _pushEventPublisher;

    public BannerService(
        SqlConnectionFactory db,
        IPushEventPublisher pushEventPublisher)
    {
        _db = db;
        _pushEventPublisher = pushEventPublisher;
    }

    public async Task<long> CreateAsync(
        BannerCreateRequest request,
        IReadOnlyCollection<BannerTargetRequest> targets,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = NormalizeCreateRequest(request, targets);

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await BannersTableExistsAsync(conn, ct))
        {
            throw new InvalidOperationException("OMP banners table is not installed.");
        }

        await using var tx = conn.BeginTransaction();
        long bannerId;
        try
        {
            const string sql = @"
INSERT INTO omp.banners
(
    title,
    content,
    status,
    level,
    starts_at,
    expires_at
)
OUTPUT INSERTED.banner_id
VALUES
(
    @title,
    @content,
    @status,
    @level,
    @starts_at,
    @expires_at
);";

            await using var cmd = new SqlCommand(sql, conn, tx);
            AddBannerParameters(cmd, normalized);
            var result = await cmd.ExecuteScalarAsync(ct);
            bannerId = Convert.ToInt64(result, CultureInfo.InvariantCulture);

            await ReplaceTargetsAsync(conn, tx, bannerId, normalized.Targets, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        await PublishBannerChangedAsync("created", bannerId, normalized.Targets, ct);
        return bannerId;
    }

    public async Task<bool> UpdateAsync(BannerEditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BannerId <= 0)
        {
            return false;
        }

        var normalized = NormalizeCreateRequest(request, request.Targets);

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await BannersTableExistsAsync(conn, ct))
        {
            return false;
        }

        await using var tx = conn.BeginTransaction();
        var updated = false;
        try
        {
            const string sql = @"
UPDATE omp.banners
SET title = @title,
    content = @content,
    status = @status,
    level = @level,
    starts_at = @starts_at,
    expires_at = @expires_at,
    updated_at = SYSUTCDATETIME()
WHERE banner_id = @banner_id;";

            await using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add("@banner_id", SqlDbType.BigInt).Value = request.BannerId;
            AddBannerParameters(cmd, normalized);
            var affected = await cmd.ExecuteNonQueryAsync(ct);
            if (affected == 0)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            await ReplaceTargetsAsync(conn, tx, request.BannerId, normalized.Targets, ct);
            await tx.CommitAsync(ct);
            updated = true;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        if (updated)
        {
            await PublishBannerChangedAsync("updated", request.BannerId, normalized.Targets, ct);
        }

        return updated;
    }

    public async Task<bool> DisableAsync(long bannerId, CancellationToken ct)
    {
        if (bannerId <= 0)
        {
            return false;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await BannersTableExistsAsync(conn, ct))
        {
            return false;
        }

        var targets = await GetTargetsAsync(conn, bannerId, ct);

        const string sql = @"
UPDATE omp.banners
SET status = N'disabled',
    updated_at = SYSUTCDATETIME()
WHERE banner_id = @banner_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@banner_id", SqlDbType.BigInt).Value = bannerId;
        var disabled = await cmd.ExecuteNonQueryAsync(ct) > 0;
        if (disabled)
        {
            await PublishBannerChangedAsync("disabled", bannerId, targets, ct);
        }

        return disabled;
    }

    public async Task<IReadOnlyList<PortalTopBarBanner>> GetActiveForRolesAsync(
        IReadOnlyCollection<int> roleIds,
        int limit,
        CancellationToken ct)
    {
        if (limit <= 0)
        {
            return Array.Empty<PortalTopBarBanner>();
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await BannersTableExistsAsync(conn, ct))
        {
            return Array.Empty<PortalTopBarBanner>();
        }

        var distinctRoleIds = roleIds
            .Where(roleId => roleId > 0)
            .Distinct()
            .ToArray();
        var rolePredicate = distinctRoleIds.Length == 0
            ? "CAST(0 AS bit) = CAST(1 AS bit)"
            : $"bt.target_type = N'role' AND bt.role_id IN ({string.Join(", ", distinctRoleIds.Select((_, index) => $"@role{index}"))})";

        var sql = $@"
SELECT TOP (@limit)
       b.banner_id,
       b.title,
       b.content,
       b.level,
       b.starts_at,
       b.expires_at
FROM omp.banners b
WHERE b.status = N'active'
  AND (b.starts_at IS NULL OR b.starts_at <= SYSUTCDATETIME())
  AND (b.expires_at IS NULL OR b.expires_at > SYSUTCDATETIME())
  AND EXISTS
  (
      SELECT 1
      FROM omp.banner_targets bt
      WHERE bt.banner_id = b.banner_id
        AND
        (
            bt.target_type = N'global'
            OR ({rolePredicate})
        )
  )
ORDER BY b.level DESC,
         b.created_at DESC,
         b.banner_id DESC;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        for (var i = 0; i < distinctRoleIds.Length; i++)
        {
            cmd.Parameters.Add($"@role{i}", SqlDbType.Int).Value = distinctRoleIds[i];
        }

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<PortalTopBarBanner>();
        while (await rdr.ReadAsync(ct))
        {
            var level = rdr.GetInt32(3);
            rows.Add(new PortalTopBarBanner(
                rdr.GetInt64(0),
                rdr.GetString(1),
                rdr.GetString(2),
                level,
                LevelName(level),
                IconUrl(level),
                rdr.IsDBNull(4) ? null : rdr.GetDateTime(4),
                rdr.IsDBNull(5) ? null : rdr.GetDateTime(5)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<BannerAdminRow>> GetAdminRowsAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await BannersTableExistsAsync(conn, ct))
        {
            return Array.Empty<BannerAdminRow>();
        }

        const string sql = @"
SELECT b.banner_id,
       b.title,
       b.content,
       b.status,
       b.level,
       b.starts_at,
       b.expires_at,
       b.created_at,
       b.updated_at,
       COALESCE
       (
           STRING_AGG
           (
               CASE
                   WHEN bt.target_type = N'global' THEN N'Global'
                   WHEN bt.target_type = N'role' THEN COALESCE(r.Name, CONCAT(N'Role ', CONVERT(nvarchar(20), bt.role_id)))
                   ELSE bt.target_type
               END,
               N', '
           ) WITHIN GROUP (ORDER BY bt.target_type, r.Name, bt.role_id),
           N''
       ) AS targets
FROM omp.banners b
LEFT JOIN omp.banner_targets bt ON bt.banner_id = b.banner_id
LEFT JOIN omp.Roles r ON r.RoleId = bt.role_id
GROUP BY b.banner_id,
         b.title,
         b.content,
         b.status,
         b.level,
         b.starts_at,
         b.expires_at,
         b.created_at,
         b.updated_at
ORDER BY b.created_at DESC,
         b.banner_id DESC;";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<BannerAdminRow>();
        while (await rdr.ReadAsync(ct))
        {
            var status = rdr.GetString(3);
            var level = rdr.GetInt32(4);
            var startsAt = rdr.IsDBNull(5) ? (DateTime?)null : rdr.GetDateTime(5);
            var expiresAt = rdr.IsDBNull(6) ? (DateTime?)null : rdr.GetDateTime(6);
            rows.Add(new BannerAdminRow(
                rdr.GetInt64(0),
                rdr.GetString(1),
                rdr.GetString(2),
                status,
                level,
                LevelName(level),
                startsAt,
                expiresAt,
                rdr.GetDateTime(7),
                rdr.GetDateTime(8),
                rdr.GetString(9),
                DisplayState(status, startsAt, expiresAt)));
        }

        return rows;
    }

    public async Task<BannerEditData?> GetForEditAsync(long bannerId, CancellationToken ct)
    {
        if (bannerId <= 0)
        {
            return null;
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        if (!await BannersTableExistsAsync(conn, ct))
        {
            return null;
        }

        const string sql = @"
SELECT banner_id,
       title,
       content,
       status,
       level,
       starts_at,
       expires_at
FROM omp.banners
WHERE banner_id = @banner_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@banner_id", SqlDbType.BigInt).Value = bannerId;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct))
        {
            return null;
        }

        var row = new BannerEditData(
            rdr.GetInt64(0),
            rdr.GetString(1),
            rdr.GetString(2),
            rdr.GetString(3),
            rdr.GetInt32(4),
            rdr.IsDBNull(5) ? null : rdr.GetDateTime(5),
            rdr.IsDBNull(6) ? null : rdr.GetDateTime(6),
            []);

        await rdr.CloseAsync();
        var targets = await GetTargetsAsync(conn, bannerId, ct);
        return row with { Targets = targets };
    }

    public async Task<IReadOnlyList<BannerRoleOption>> GetRoleOptionsAsync(CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT RoleId,
       Name
FROM omp.Roles
ORDER BY Name,
         RoleId;";

        await using var cmd = new SqlCommand(sql, conn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<BannerRoleOption>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new BannerRoleOption(rdr.GetInt32(0), rdr.GetString(1)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<int>> GetRoleIdsWithPermissionAsync(
        string permissionName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(permissionName))
        {
            return Array.Empty<int>();
        }

        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        const string sql = @"
SELECT DISTINCT rp.RoleId
FROM omp.RolePermissions rp
INNER JOIN omp.Permissions p ON p.PermissionId = rp.PermissionId
WHERE p.Name = @permission_name
ORDER BY rp.RoleId;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@permission_name", SqlDbType.NVarChar, 200).Value = permissionName.Trim();

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<int>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(rdr.GetInt32(0));
        }

        return rows;
    }

    public static string LevelName(int level)
        => level switch
        {
            LevelCritical => "critical",
            LevelWarning => "warning",
            _ => "announcement"
        };

    public static string DisplayState(string? status, DateTime? startsAtUtc, DateTime? expiresAtUtc)
    {
        if (!string.Equals(status, StatusActive, StringComparison.OrdinalIgnoreCase))
        {
            return "disabled";
        }

        var now = DateTime.UtcNow;
        if (startsAtUtc.HasValue && startsAtUtc.Value > now)
        {
            return "scheduled";
        }

        if (expiresAtUtc.HasValue && expiresAtUtc.Value <= now)
        {
            return "expired";
        }

        return "active";
    }

    private async Task PublishBannerChangedAsync(
        string action,
        long bannerId,
        IReadOnlyCollection<BannerTargetRequest> targets,
        CancellationToken ct)
    {
        foreach (var pushEvent in CreateBannerChangedPushEvents(action, bannerId, targets))
        {
            await _pushEventPublisher.PublishAsync(pushEvent, ct);
        }
    }

    internal static IReadOnlyList<PushEvent> CreateBannerChangedPushEvents(
        string action,
        long bannerId,
        IReadOnlyCollection<BannerTargetRequest> targets)
    {
        var payloadJson = JsonSerializer.Serialize(new
        {
            action,
            bannerId
        });

        if (targets.Any(target => string.Equals(target.TargetType, TargetGlobal, StringComparison.OrdinalIgnoreCase)))
        {
            return
            [
                PushEvent.ForBroadcast(
                    PushEventCategory.TopBarBannerStateChanged,
                    payloadJson,
                    correlationKey: string.Create(CultureInfo.InvariantCulture, $"banner:{bannerId}"))
            ];
        }

        var roleIds = targets
            .Where(target => string.Equals(target.TargetType, TargetRole, StringComparison.OrdinalIgnoreCase))
            .Select(target => target.RoleId)
            .Where(roleId => roleId.HasValue && roleId.Value > 0)
            .Select(roleId => roleId!.Value.ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToArray();

        if (roleIds.Length == 0)
        {
            return [];
        }

        return
        [
            new PushEvent(
                PushEventCategory.TopBarBannerStateChanged,
                new PushTarget(PushTargetKind.Role, roleIds),
                payloadJson,
                CorrelationKey: string.Create(CultureInfo.InvariantCulture, $"banner:{bannerId}"))
        ];
    }

    private static async Task ReplaceTargetsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        long bannerId,
        IReadOnlyCollection<BannerTargetRequest> targets,
        CancellationToken ct)
    {
        await using (var deleteCmd = new SqlCommand("DELETE FROM omp.banner_targets WHERE banner_id = @banner_id;", conn, tx))
        {
            deleteCmd.Parameters.Add("@banner_id", SqlDbType.BigInt).Value = bannerId;
            await deleteCmd.ExecuteNonQueryAsync(ct);
        }

        const string insertSql = @"
INSERT INTO omp.banner_targets
(
    banner_id,
    target_type,
    role_id
)
VALUES
(
    @banner_id,
    @target_type,
    @role_id
);";

        foreach (var target in targets)
        {
            await using var insertCmd = new SqlCommand(insertSql, conn, tx);
            insertCmd.Parameters.Add("@banner_id", SqlDbType.BigInt).Value = bannerId;
            insertCmd.Parameters.Add("@target_type", SqlDbType.NVarChar, 40).Value = target.TargetType;
            insertCmd.Parameters.Add("@role_id", SqlDbType.Int).Value = target.RoleId.HasValue
                ? target.RoleId.Value
                : DBNull.Value;
            await insertCmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<IReadOnlyList<BannerTargetRequest>> GetTargetsAsync(
        SqlConnection conn,
        long bannerId,
        CancellationToken ct)
    {
        const string sql = @"
SELECT target_type,
       role_id
FROM omp.banner_targets
WHERE banner_id = @banner_id
ORDER BY target_type,
         role_id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@banner_id", SqlDbType.BigInt).Value = bannerId;

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<BannerTargetRequest>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new BannerTargetRequest(
                rdr.GetString(0),
                rdr.IsDBNull(1) ? null : rdr.GetInt32(1)));
        }

        return rows;
    }

    private static NormalizedBannerRequest NormalizeCreateRequest(
        BannerCreateRequest request,
        IReadOnlyCollection<BannerTargetRequest> targets)
    {
        var title = CleanRequired(request.Title, nameof(request.Title), 200);
        var content = CleanRequired(request.Content, nameof(request.Content), 1000);
        var status = NormalizeStatus(request.Status);
        var level = Math.Clamp(request.Level, LevelAnnouncement, LevelCritical);
        var startsAt = ToUtcNullable(request.StartsAtUtc);
        var expiresAt = ToUtcNullable(request.ExpiresAtUtc);
        if (startsAt.HasValue && expiresAt.HasValue && expiresAt.Value <= startsAt.Value)
        {
            throw new ArgumentException("Banner expiration must be after the start time.", nameof(request));
        }

        var normalizedTargets = NormalizeTargets(targets);
        return new NormalizedBannerRequest(title, content, status, level, startsAt, expiresAt, normalizedTargets);
    }

    private static IReadOnlyList<BannerTargetRequest> NormalizeTargets(IReadOnlyCollection<BannerTargetRequest> targets)
    {
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one banner target is required.", nameof(targets));
        }

        var hasGlobal = targets.Any(target => string.Equals(target.TargetType, TargetGlobal, StringComparison.OrdinalIgnoreCase));
        if (hasGlobal)
        {
            if (targets.Count != 1)
            {
                throw new ArgumentException("Global banners cannot be combined with role targets.", nameof(targets));
            }

            return [new BannerTargetRequest(TargetGlobal, null)];
        }

        var roleIds = targets
            .Where(target => string.Equals(target.TargetType, TargetRole, StringComparison.OrdinalIgnoreCase))
            .Select(target => target.RoleId)
            .Where(roleId => roleId.HasValue && roleId.Value > 0)
            .Select(roleId => roleId!.Value)
            .Distinct()
            .Order()
            .ToArray();

        if (roleIds.Length == 0)
        {
            throw new ArgumentException("At least one role target is required.", nameof(targets));
        }

        return roleIds
            .Select(roleId => new BannerTargetRequest(TargetRole, roleId))
            .ToArray();
    }

    private static void AddBannerParameters(SqlCommand cmd, NormalizedBannerRequest request)
    {
        cmd.Parameters.Add("@title", SqlDbType.NVarChar, 200).Value = request.Title;
        cmd.Parameters.Add("@content", SqlDbType.NVarChar, 1000).Value = request.Content;
        cmd.Parameters.Add("@status", SqlDbType.NVarChar, 40).Value = request.Status;
        cmd.Parameters.Add("@level", SqlDbType.Int).Value = request.Level;
        cmd.Parameters.Add("@starts_at", SqlDbType.DateTime2).Value = request.StartsAtUtc.HasValue
            ? request.StartsAtUtc.Value
            : DBNull.Value;
        cmd.Parameters.Add("@expires_at", SqlDbType.DateTime2).Value = request.ExpiresAtUtc.HasValue
            ? request.ExpiresAtUtc.Value
            : DBNull.Value;
    }

    private static async Task<bool> BannersTableExistsAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = "SELECT CAST(CASE WHEN OBJECT_ID(N'omp.banners', N'U') IS NULL OR OBJECT_ID(N'omp.banner_targets', N'U') IS NULL THEN 0 ELSE 1 END AS bit);";
        await using var cmd = new SqlCommand(sql, conn);
        return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static string NormalizeStatus(string? status)
    {
        var normalized = string.IsNullOrWhiteSpace(status)
            ? StatusActive
            : status.Trim().ToLowerInvariant();

        return normalized is StatusActive or StatusDisabled ? normalized : StatusActive;
    }

    private static string CleanRequired(string? value, string parameterName, int maxLength)
    {
        var cleaned = value?.Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static DateTime? ToUtcNullable(DateTimeOffset? value)
        => value?.UtcDateTime;

    private static string IconUrl(int level)
        => level == LevelAnnouncement ? InfoIconUrl : WarningIconUrl;

    private sealed record NormalizedBannerRequest(
        string Title,
        string Content,
        string Status,
        int Level,
        DateTime? StartsAtUtc,
        DateTime? ExpiresAtUtc,
        IReadOnlyList<BannerTargetRequest> Targets);
}

public record BannerCreateRequest(
    string? Title,
    string? Content,
    string? Status = BannerService.StatusActive,
    int Level = BannerService.LevelAnnouncement,
    DateTimeOffset? StartsAtUtc = null,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record BannerEditRequest(
    long BannerId,
    string? Title,
    string? Content,
    string? Status,
    int Level,
    DateTimeOffset? StartsAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    IReadOnlyCollection<BannerTargetRequest> Targets)
    : BannerCreateRequest(Title, Content, Status, Level, StartsAtUtc, ExpiresAtUtc);

public sealed record BannerTargetRequest(string TargetType, int? RoleId);

public sealed record BannerAdminRow(
    long BannerId,
    string Title,
    string Content,
    string Status,
    int Level,
    string LevelName,
    DateTime? StartsAtUtc,
    DateTime? ExpiresAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string TargetSummary,
    string DisplayState);

public sealed record BannerEditData(
    long BannerId,
    string Title,
    string Content,
    string Status,
    int Level,
    DateTime? StartsAtUtc,
    DateTime? ExpiresAtUtc,
    IReadOnlyList<BannerTargetRequest> Targets);

public sealed record BannerRoleOption(int RoleId, string Name);
