using Microsoft.Data.SqlClient;
using OpenModulePlatform.EventPublisher;
using OpenModulePlatform.EventPublisher.Sql;
using System.Data;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class SqlPushEventPublisherTests
{
    [Fact]
    public void CreatePublishCommand_MapsValidatedEventToOutboxRow()
    {
        var scheduledAtUtc = new DateTimeOffset(2026, 6, 24, 10, 30, 0, TimeSpan.Zero);
        var pushEvent = new PushEvent(
            PushEventCategory.TopBarMessageStateChanged,
            PushTarget.ForUser(42),
            PayloadJson: """{"conversationId":123}""",
            DeduplicationKey: "message:123:42",
            CorrelationKey: "conversation:123",
            ScheduledAtUtc: scheduledAtUtc,
            MaxRetries: 7).Normalize();

        using var conn = new SqlConnection("Server=(local);Database=OpenModulePlatform;Integrated Security=true;TrustServerCertificate=true");
        using var cmd = SqlPushEventPublisher.CreatePublishCommand(conn, pushEvent);

        Assert.Contains("INSERT INTO omp.push_event_outbox", cmd.CommandText);
        Assert.Contains("deduplication_key", cmd.CommandText);
        Assert.Contains("correlation_key", cmd.CommandText);
        Assert.Contains("max_retries", cmd.CommandText);

        AssertParameter(cmd, "@event_category", SqlDbType.NVarChar, PushEvent.MaxEventCategoryLength, "topbar.message-state-changed");
        AssertParameter(cmd, "@target_type", SqlDbType.NVarChar, 40, "user");
        AssertParameter(cmd, "@target_user_id", SqlDbType.Int, 0, 42);
        AssertParameter(cmd, "@target_json", SqlDbType.NVarChar, PushEvent.MaxTargetJsonLength, """{"kind":"user","ids":["42"]}""");
        AssertParameter(cmd, "@payload_json", SqlDbType.NVarChar, PushEvent.MaxPayloadJsonLength, """{"conversationId":123}""");
        AssertParameter(cmd, "@deduplication_key", SqlDbType.NVarChar, PushEvent.MaxKeyLength, "message:123:42");
        AssertParameter(cmd, "@correlation_key", SqlDbType.NVarChar, PushEvent.MaxKeyLength, "conversation:123");
        AssertParameter(cmd, "@max_retries", SqlDbType.Int, 0, 7);
        AssertParameter(cmd, "@scheduled_utc", SqlDbType.DateTime2, 0, scheduledAtUtc.UtcDateTime);
    }

    private static void AssertParameter(
        SqlCommand cmd,
        string name,
        SqlDbType sqlDbType,
        int size,
        object expectedValue)
    {
        var parameter = Assert.IsType<SqlParameter>(cmd.Parameters[name]);

        Assert.Equal(sqlDbType, parameter.SqlDbType);
        if (size > 0)
        {
            Assert.Equal(size, parameter.Size);
        }

        Assert.Equal(expectedValue, parameter.Value);
    }
}
