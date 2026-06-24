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

            const string sql = @"
INSERT INTO omp.push_event_outbox
(
    event_category,
    target_type,
    target_user_id,
    payload_json
)
OUTPUT INSERTED.push_event_id
VALUES
(
    @event_category,
    @target_type,
    @target_user_id,
    @payload_json
);";

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@event_category", SqlDbType.NVarChar, PushEvent.MaxEventCategoryLength).Value = normalized.EventCategory;
            cmd.Parameters.Add("@target_type", SqlDbType.NVarChar, 40).Value = normalized.TargetType;
            cmd.Parameters.Add("@target_user_id", SqlDbType.Int).Value = normalized.TargetUserId.HasValue
                ? normalized.TargetUserId.Value
                : DBNull.Value;
            cmd.Parameters.Add("@payload_json", SqlDbType.NVarChar, PushEvent.MaxPayloadJsonLength).Value = normalized.PayloadJson is null
                ? DBNull.Value
                : normalized.PayloadJson;

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
}
