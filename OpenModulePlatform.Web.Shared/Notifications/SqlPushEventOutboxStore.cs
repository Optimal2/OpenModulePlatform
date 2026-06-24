using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Services;
using System.Data;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Notifications;

internal sealed class SqlPushEventOutboxStore
{
    private readonly SqlConnectionFactory _db;

    public SqlPushEventOutboxStore(SqlConnectionFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<LeasedPushEvent>> AcquireLeaseAsync(
        PushEventDispatcherOptions options,
        string leaseOwner,
        Guid leaseToken,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = CreateAcquireLeaseCommand(conn, options, leaseOwner, leaseToken);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<LeasedPushEvent>();
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new LeasedPushEvent(
                rdr.GetInt64(0),
                rdr.GetString(1),
                rdr.GetString(2),
                rdr.IsDBNull(3) ? null : rdr.GetInt32(3),
                rdr.GetString(4),
                rdr.IsDBNull(5) ? null : rdr.GetString(5),
                rdr.IsDBNull(6) ? null : rdr.GetString(6),
                rdr.IsDBNull(7) ? null : rdr.GetString(7),
                rdr.GetGuid(8),
                rdr.GetDateTime(9)));
        }

        return rows;
    }

    public async Task MarkDispatchedAsync(LeasedPushEvent pushEvent, CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = CreateMarkDispatchedCommand(conn, pushEvent);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkFailedAsync(
        LeasedPushEvent pushEvent,
        PushEventDispatcherOptions options,
        Exception exception,
        CancellationToken ct)
    {
        await using var conn = _db.Create();
        await conn.OpenAsync(ct);

        await using var cmd = CreateMarkFailedCommand(conn, pushEvent, options, exception);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static SqlCommand CreateAcquireLeaseCommand(
        SqlConnection conn,
        PushEventDispatcherOptions options,
        string leaseOwner,
        Guid leaseToken)
    {
        const string sql = @"
SET NOCOUNT ON;
DECLARE @now_utc datetime2(3) = SYSUTCDATETIME();
DECLARE @lease_until_utc datetime2(3) = DATEADD(SECOND, @lease_seconds, @now_utc);

;WITH next_events AS
(
    SELECT TOP (@batch_size) push_event_id
    FROM omp.push_event_outbox WITH (READPAST, UPDLOCK, ROWLOCK)
    WHERE scheduled_utc <= @now_utc
      AND completed_utc IS NULL
      AND dead_lettered_utc IS NULL
      AND
      (
          status = N'pending'
          OR
          (
              status = N'processing'
              AND (lease_until_utc IS NULL OR lease_until_utc <= @now_utc)
          )
      )
    ORDER BY push_event_id ASC
)
UPDATE outbox
SET status = N'processing',
    lease_token = @lease_token,
    lease_owner = @lease_owner,
    lease_until_utc = @lease_until_utc,
    error_message = NULL
OUTPUT inserted.push_event_id,
       inserted.event_category,
       inserted.target_type,
       inserted.target_user_id,
       inserted.target_json,
       inserted.payload_json,
       inserted.deduplication_key,
       inserted.correlation_key,
       inserted.lease_token,
       inserted.created_utc
FROM omp.push_event_outbox outbox
INNER JOIN next_events leased
    ON leased.push_event_id = outbox.push_event_id;";

        var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@batch_size", SqlDbType.Int).Value = options.EffectiveBatchSize;
        cmd.Parameters.Add("@lease_seconds", SqlDbType.Int).Value = options.EffectiveLeaseSeconds;
        cmd.Parameters.Add("@lease_token", SqlDbType.UniqueIdentifier).Value = leaseToken;
        cmd.Parameters.Add("@lease_owner", SqlDbType.NVarChar, 200).Value = Truncate(leaseOwner, 200);
        return cmd;
    }

    internal static SqlCommand CreateMarkDispatchedCommand(SqlConnection conn, LeasedPushEvent pushEvent)
    {
        const string sql = @"
UPDATE omp.push_event_outbox
SET status = N'dispatched',
    dispatched_utc = SYSUTCDATETIME(),
    completed_utc = SYSUTCDATETIME(),
    lease_token = NULL,
    lease_owner = NULL,
    lease_until_utc = NULL,
    error_message = NULL
WHERE push_event_id = @push_event_id
  AND lease_token = @lease_token;";

        var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@push_event_id", SqlDbType.BigInt).Value = pushEvent.PushEventId;
        cmd.Parameters.Add("@lease_token", SqlDbType.UniqueIdentifier).Value = pushEvent.LeaseToken;
        return cmd;
    }

    internal static SqlCommand CreateMarkFailedCommand(
        SqlConnection conn,
        LeasedPushEvent pushEvent,
        PushEventDispatcherOptions options,
        Exception exception)
    {
        const string sql = @"
DECLARE @now_utc datetime2(3) = SYSUTCDATETIME();

UPDATE omp.push_event_outbox
SET retry_count = retry_count + 1,
    status = CASE WHEN retry_count + 1 > max_retries THEN N'dead-lettered' ELSE N'pending' END,
    scheduled_utc = CASE WHEN retry_count + 1 > max_retries THEN scheduled_utc ELSE DATEADD(SECOND, @retry_delay_seconds, @now_utc) END,
    dead_lettered_utc = CASE WHEN retry_count + 1 > max_retries THEN @now_utc ELSE NULL END,
    lease_token = NULL,
    lease_owner = NULL,
    lease_until_utc = NULL,
    error_message = @error_message
WHERE push_event_id = @push_event_id
  AND lease_token = @lease_token;";

        var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@push_event_id", SqlDbType.BigInt).Value = pushEvent.PushEventId;
        cmd.Parameters.Add("@lease_token", SqlDbType.UniqueIdentifier).Value = pushEvent.LeaseToken;
        cmd.Parameters.Add("@retry_delay_seconds", SqlDbType.Int).Value = options.EffectiveRetryDelaySeconds;
        cmd.Parameters.Add("@error_message", SqlDbType.NVarChar, 2048).Value =
            Truncate(exception.Message, options.EffectiveMaxErrorMessageLength);
        return cmd;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
