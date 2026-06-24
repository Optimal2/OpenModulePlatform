using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Notifications;
using System.Data;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class PushEventDispatcherTests
{
    [Fact]
    public void CreateAcquireLeaseCommand_UsesAtomicOrderedLease()
    {
        var options = new PushEventDispatcherOptions
        {
            BatchSize = 25,
            LeaseSeconds = 45
        };
        using var conn = new SqlConnection("Server=(local);Database=OpenModulePlatform;Integrated Security=true;TrustServerCertificate=true");

        using var cmd = SqlPushEventOutboxStore.CreateAcquireLeaseCommand(
            conn,
            options,
            "test-owner",
            Guid.Parse("11111111-1111-1111-1111-111111111111"));

        Assert.Contains("WITH (READPAST, UPDLOCK, ROWLOCK)", cmd.CommandText);
        Assert.Contains("ORDER BY push_event_id ASC", cmd.CommandText);
        Assert.Contains("UPDATE outbox", cmd.CommandText);
        Assert.Contains("OUTPUT inserted.push_event_id", cmd.CommandText);
        Assert.Contains("status = N'processing'", cmd.CommandText);
        AssertParameter(cmd, "@batch_size", SqlDbType.Int, 25);
        AssertParameter(cmd, "@lease_seconds", SqlDbType.Int, 45);
        AssertParameter(cmd, "@lease_owner", SqlDbType.NVarChar, "test-owner");
    }

    [Fact]
    public void CreateMarkFailedCommand_RetriesThenDeadLetters()
    {
        var pushEvent = CreateLeasedEvent(targetJson: """{"kind":"user","ids":["42"]}""");
        using var conn = new SqlConnection("Server=(local);Database=OpenModulePlatform;Integrated Security=true;TrustServerCertificate=true");

        using var cmd = SqlPushEventOutboxStore.CreateMarkFailedCommand(
            conn,
            pushEvent,
            new PushEventDispatcherOptions { RetryDelaySeconds = 30 },
            new InvalidOperationException("dispatch failed"));

        Assert.Contains("retry_count = retry_count + 1", cmd.CommandText);
        Assert.Contains("status = CASE WHEN retry_count + 1 > max_retries THEN N'dead-lettered' ELSE N'pending' END", cmd.CommandText);
        Assert.Contains("dead_lettered_utc = CASE WHEN retry_count + 1 > max_retries THEN @now_utc ELSE NULL END", cmd.CommandText);
        AssertParameter(cmd, "@retry_delay_seconds", SqlDbType.Int, 30);
        AssertParameter(cmd, "@error_message", SqlDbType.NVarChar, "dispatch failed");
    }

    [Theory]
    [InlineData("user", """{"kind":"user","ids":["42"]}""", "omp-user:42")]
    [InlineData("role", """{"kind":"role","ids":["7"]}""", "omp-role:7")]
    [InlineData("broadcast", """{"kind":"broadcast","ids":[]}""", "omp-broadcast")]
    [InlineData("authenticated", """{"kind":"authenticated","ids":[]}""", "omp-authenticated")]
    [InlineData("app", """{"kind":"app","ids":["omp_portal"]}""", "omp-app:omp_portal")]
    [InlineData("module", """{"kind":"module","ids":["omp_portal"]}""", "omp-module:omp_portal")]
    public void ResolveTargetGroups_MapsOutboxTargetToSignalRGroup(
        string targetType,
        string targetJson,
        string expectedGroup)
    {
        var pushEvent = CreateLeasedEvent(targetType, targetJson);

        var groups = PushEventDispatcherHostedService.ResolveTargetGroups(pushEvent);

        Assert.Equal([expectedGroup], groups);
    }

    [Fact]
    public void Envelope_UsesEventIdDedupFallbackAndParsesPayload()
    {
        var pushEvent = CreateLeasedEvent(
            targetType: "user",
            targetJson: """{"kind":"user","ids":["42"]}""",
            payloadJson: """{"refresh":true}""");

        var envelope = TopBarPushEventEnvelope.FromLeasedEvent(pushEvent);

        Assert.Equal(123, envelope.EventId);
        Assert.Equal("123", envelope.DeduplicationKey);
        Assert.Equal("topbar.notification-state-changed", envelope.Category);
        Assert.Equal("user", envelope.TargetKind);
        Assert.Equal("42", envelope.TargetValue);
        Assert.True(envelope.Payload?.GetProperty("refresh").GetBoolean());
    }

    [Fact]
    public void PortalTopbarScript_HandlesEnvelopeDedupAndOldNoArgumentSignal()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "OpenModulePlatform.Web.Shared", "wwwroot", "js", "portal-topbar.js"));

        Assert.Contains("function handleTopbarPushEvent(envelope)", script);
        Assert.Contains("rememberTopbarPushEvent(eventKey)", script);
        Assert.Contains("connection.on(TOPBAR_NOTIFICATION_PUSH_METHOD, function (envelope)", script);
        Assert.Contains("if (!envelope || typeof envelope !== 'object')", script);
        Assert.Contains("runTopbarSummaryRefreshSoon(true);", script);
    }

    private static LeasedPushEvent CreateLeasedEvent(
        string targetType = "user",
        string targetJson = """{"kind":"user","ids":["42"]}""",
        string? payloadJson = null)
        => new(
            123,
            "topbar.notification-state-changed",
            targetType,
            targetType == "user" ? 42 : null,
            targetJson,
            payloadJson,
            null,
            null,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new DateTime(2026, 6, 24, 10, 30, 0, DateTimeKind.Utc));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenModulePlatform.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
    }

    private static void AssertParameter(SqlCommand cmd, string name, SqlDbType sqlDbType, object expectedValue)
    {
        var parameter = Assert.IsType<SqlParameter>(cmd.Parameters[name]);

        Assert.Equal(sqlDbType, parameter.SqlDbType);
        Assert.Equal(expectedValue, parameter.Value);
    }
}
