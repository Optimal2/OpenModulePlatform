using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.EventPublisher;
using System.Data;
using System.Globalization;

namespace OpenModulePlatform.EventPublisher.Sql;

public sealed class SqlPushEventPublisher : IPushEventPublisher
{
    private readonly Func<SqlConnection> _createConnection;
    private readonly ILogger<SqlPushEventPublisher> _logger;

    public SqlPushEventPublisher(
        Func<SqlConnection> createConnection,
        ILogger<SqlPushEventPublisher> logger)
    {
        _createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
        _logger = logger;
    }

    public async Task<long> PublishAsync(PushEvent pushEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pushEvent);

        var normalized = pushEvent.Normalize();

        try
        {
            await using var conn = _createConnection();
            await conn.OpenAsync(ct);

            await using var cmd = CreatePublishCommand(conn, normalized);

            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish OMP push event {EventCategory} for target type {TargetType}.",
                normalized.EventCategory,
                normalized.TargetType);
            throw;
        }
    }

    internal static SqlCommand CreatePublishCommand(SqlConnection conn, PushEvent normalized)
    {
        const string sql = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DECLARE @push_event_id bigint = NULL;

IF @deduplication_key IS NOT NULL
BEGIN
    SELECT @push_event_id = push_event_id
    FROM omp.push_event_outbox WITH (UPDLOCK, HOLDLOCK)
    WHERE deduplication_key = @deduplication_key;
END;

IF @push_event_id IS NULL
BEGIN
INSERT INTO omp.push_event_outbox
(
    event_category,
    target_type,
    target_user_id,
    target_json,
    payload_json,
    deduplication_key,
    correlation_key,
    max_retries,
    scheduled_utc
)
OUTPUT INSERTED.push_event_id
VALUES
(
    @event_category,
    @target_type,
    @target_user_id,
    @target_json,
    @payload_json,
    @deduplication_key,
    @correlation_key,
    @max_retries,
    COALESCE(@scheduled_utc, SYSUTCDATETIME())
);

SET @push_event_id = CONVERT(bigint, SCOPE_IDENTITY());
END;

COMMIT TRANSACTION;

SELECT @push_event_id;";

        var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@event_category", SqlDbType.NVarChar, PushEvent.MaxEventCategoryLength).Value = normalized.EventCategory;
        cmd.Parameters.Add("@target_type", SqlDbType.NVarChar, 40).Value = normalized.TargetType;
        cmd.Parameters.Add("@target_user_id", SqlDbType.Int).Value = normalized.TargetUserId.HasValue
            ? normalized.TargetUserId.Value
            : DBNull.Value;
        cmd.Parameters.Add("@target_json", SqlDbType.NVarChar, PushEvent.MaxTargetJsonLength).Value = normalized.TargetJson;
        cmd.Parameters.Add("@payload_json", SqlDbType.NVarChar, PushEvent.MaxPayloadJsonLength).Value = normalized.PayloadJson is null
            ? DBNull.Value
            : normalized.PayloadJson;
        cmd.Parameters.Add("@deduplication_key", SqlDbType.NVarChar, PushEvent.MaxKeyLength).Value = normalized.DeduplicationKey is null
            ? DBNull.Value
            : normalized.DeduplicationKey;
        cmd.Parameters.Add("@correlation_key", SqlDbType.NVarChar, PushEvent.MaxKeyLength).Value = normalized.CorrelationKey is null
            ? DBNull.Value
            : normalized.CorrelationKey;
        cmd.Parameters.Add("@max_retries", SqlDbType.Int).Value = normalized.MaxRetries;
        cmd.Parameters.Add("@scheduled_utc", SqlDbType.DateTime2).Value = normalized.ScheduledAtUtc.HasValue
            ? normalized.ScheduledAtUtc.Value.UtcDateTime
            : DBNull.Value;

        return cmd;
    }
}
