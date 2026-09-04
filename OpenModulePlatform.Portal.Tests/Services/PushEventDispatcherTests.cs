using Microsoft.Data.SqlClient;
using OpenModulePlatform.Web.Shared.Notifications;
using System.Data;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class PushEventDispatcherTests
{
    private const string CommandOnlyConnectionString =
        "Server=(local);Database=OpenModulePlatform;Integrated Security=true;Encrypt=false";

    [Fact]
    public void CreateAcquireLeaseCommand_UsesAtomicOrderedLease()
    {
        var options = new PushEventDispatcherOptions
        {
            BatchSize = 25,
            LeaseSeconds = 45
        };
        using var conn = CreateCommandOnlyConnection();

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
        using var conn = CreateCommandOnlyConnection();

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

    [Fact]
    public void CreateCleanupExpiredCommand_DeletesOnlyTerminalRowsWithBoundedBatch()
    {
        var options = new PushEventDispatcherOptions
        {
            CleanupBatchSize = 250,
            DispatchedRetentionDays = 3,
            FailedRetentionDays = 14
        };
        using var conn = CreateCommandOnlyConnection();

        using var cmd = SqlPushEventOutboxStore.CreateCleanupExpiredCommand(conn, options);

        Assert.Contains("DELETE TOP (@batch_size)", cmd.CommandText);
        Assert.Contains("status = N'dispatched'", cmd.CommandText);
        Assert.Contains("completed_utc IS NOT NULL", cmd.CommandText);
        Assert.Contains("status IN (N'failed', N'dead-lettered')", cmd.CommandText);
        Assert.DoesNotContain("status = N'pending'", cmd.CommandText);
        Assert.DoesNotContain("status = N'processing'", cmd.CommandText);
        AssertParameter(cmd, "@batch_size", SqlDbType.Int, 250);
        AssertParameter(cmd, "@dispatched_retention_days", SqlDbType.Int, 3);
        AssertParameter(cmd, "@failed_retention_days", SqlDbType.Int, 14);
    }

    [Theory]
    [InlineData("user", """{"kind":"user","ids":["42"]}""", "omp-user:42")]
    [InlineData("role", """{"kind":"role","ids":["7"]}""", "omp-role:7")]
    [InlineData("broadcast", """{"kind":"broadcast","ids":[]}""", "omp-broadcast")]
    [InlineData("authenticated", """{"kind":"authenticated","ids":[]}""", "omp-authenticated")]
    [InlineData("app", """{"kind":"app","ids":["omp_portal"]}""", "omp-app:omp_portal")]
    // Module targets are delivered to the authenticated group; module clients
    // scope the event by the payload "module" discriminator instead.
    [InlineData("module", """{"kind":"module","ids":["earkiv_checker"]}""", "omp-authenticated")]
    public void ResolveTargetGroups_MapsOutboxTargetToSignalRGroup(
        string targetType,
        string targetJson,
        string expectedGroup)
    {
        var pushEvent = CreateLeasedEvent(targetType, targetJson);

        var groups = PushEventDispatcherHostedService.ResolveTargetGroups(pushEvent);

        Assert.Equal([expectedGroup], groups);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("notification", true)]
    [InlineData("message", true)]
    [InlineData("topbar.notification-state-changed", true)]
    [InlineData("topbar.message-state-changed", true)]
    [InlineData("topbar.banner-state-changed", false)]
    [InlineData("module.state-changed", false)]
    [InlineData("module.specific", false)]
    public void PortalTopbarScript_RefreshesOnlySummaryCategories(string? category, bool expectedRefresh)
    {
        var script = ReadRepositoryTextFile("OpenModulePlatform.Web.Shared", "wwwroot", "js", "portal-topbar.js");
        var start = script.IndexOf("function isTopbarSummaryPushCategory(category)", StringComparison.Ordinal);
        var end = script.IndexOf("function handleTopbarPushEvent(envelope)", StringComparison.Ordinal);
        var functionBody = script[start..end];

        if (category is null)
        {
            Assert.Contains("return true;", functionBody);
            return;
        }

        if (expectedRefresh)
        {
            Assert.Contains(category, functionBody);
        }
        else
        {
            Assert.DoesNotContain(category, functionBody);
        }
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
        var script = ReadRepositoryTextFile("OpenModulePlatform.Web.Shared", "wwwroot", "js", "portal-topbar.js");

        Assert.Contains("function handleTopbarPushEvent(envelope)", script);
        Assert.Contains("rememberTopbarPushEvent(eventKey)", script);
        Assert.Contains("connection.on(TOPBAR_NOTIFICATION_PUSH_METHOD, function (envelope)", script);
        Assert.Contains("connection.on(GENERIC_PUSH_EVENT_METHOD, function (envelope)", script);
        Assert.Contains("if (!envelope || typeof envelope !== 'object')", script);
        Assert.Contains("window.dispatchEvent(new CustomEvent(PUSH_EVENT_RECEIVED_EVENT", script);
        Assert.True(
            script.IndexOf("if (rememberTopbarPushEvent(eventKey))", StringComparison.Ordinal)
            < script.IndexOf("dispatchOmpPushEvent(envelope);", StringComparison.Ordinal));
        Assert.Contains("runTopbarSummaryRefreshSoon(true);", script);
    }

    [Fact]
    public void PortalNotificationToastScript_RefreshesFromTopbarPushEvent()
    {
        var script = ReadRepositoryTextFile("OpenModulePlatform.Web.Shared", "wwwroot", "js", "omp-toasts.js");

        Assert.Contains("""var pushEventName = "omp:push-event";""", script);
        Assert.Contains("window.addEventListener(pushEventName, handlePushEvent);", script);
        Assert.Contains("function isToastPushCategory(category)", script);
        Assert.Contains("""topbar.notification-state-changed""", script);
        Assert.Contains("""topbar.message-state-changed""", script);
        Assert.Contains("function playNotificationSound()", script);
        Assert.Contains("function playMessageNotificationSound(shouldSuppress)", script);
        Assert.Contains("window.ompToastSound", script);
        Assert.Contains("""[data-message-thread-composer]""", script);
        Assert.Contains("playNotificationSound();", script);
        Assert.Contains("playMessageNotificationSound(false);", script);
        Assert.Contains("scheduleNext(0);", script);
    }

    [Fact]
    public void PortalMessageThreadPage_FollowsTopbarUpdateModeAndFiltersPushByConversation()
    {
        var page = ReadRepositoryTextFile("OpenModulePlatform.Portal", "Pages", "Messages", "Thread.cshtml");
        // The page script moved out of the .cshtml to a static file so the Portal
        // script-src can drop 'unsafe-inline' (campaign csp-vagen-till-enforcement).
        var script = ReadRepositoryTextFile("OpenModulePlatform.Portal", "wwwroot", "js", "message-thread-page.js");
        var pageModel = ReadRepositoryTextFile("OpenModulePlatform.Portal", "Pages", "Messages", "Thread.cshtml.cs");

        Assert.Contains("data-refresh-url", page);
        Assert.Contains("""<script src="~/js/message-thread-page.js"></script>""", page);
        Assert.Contains("data-notification-update-mode", script);
        Assert.Contains("data-notification-poll-interval", script);
        Assert.Contains("form.addEventListener('submit', submitMessage);", script);
        Assert.Contains("'Accept': 'text/html'", script);
        Assert.Contains("replaceMessages(html, { stickToBottom: true, suppressSound: true })", script);
        Assert.Contains("window.ompToastSound?.playMessage(false);", script);
        Assert.Contains("const PUSH_EVENT_NAME = 'omp:push-event';", script);
        Assert.Contains("const MESSAGE_PUSH_CATEGORY = 'topbar.message-state-changed';", script);
        Assert.Contains("getPayloadConversationId(payload) !== conversationId", script);
        Assert.Contains("config.mode !== UPDATE_PUSH_MODE", script);
        Assert.Contains("config.mode !== UPDATE_POLL_MODE", script);
        Assert.Contains("public async Task<IActionResult> OnGetMessages", pageModel);
        Assert.Contains("return Partial(MessagesPartialName, this);", pageModel);
        Assert.Contains("await LoadAsync(userId, conversationId, beforeMessageId: null, markRead: true, ct);", pageModel);
    }

    [Fact]
    public void OmpLiveRefreshScript_LogsWhenFallbackPollingEngages()
    {
        // The fallback poller may stay, but it must never silently rescue a
        // broken push path again: engaging it has to be visible in the console.
        var script = ReadRepositoryTextFile("OpenModulePlatform.Web.Shared", "wwwroot", "js", "omp-live-refresh.js");
        var start = script.IndexOf("function scheduleFallback(subscription)", StringComparison.Ordinal);
        var end = script.IndexOf("// --- state propagation", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "scheduleFallback must exist before the state propagation section.");
        var functionBody = script[start..end];

        Assert.Contains("subscription.fallbackWarned", functionBody);
        Assert.Contains("window.console.warn(", functionBody);
        Assert.Contains("no live push transport", functionBody);
        Assert.Contains("source: 'fallback'", functionBody);
    }

    [Fact]
    public void OmpLiveRefreshScript_ReArmsFallbackWarningWhenPushReturns()
    {
        var script = ReadRepositoryTextFile("OpenModulePlatform.Web.Shared", "wwwroot", "js", "omp-live-refresh.js");
        var start = script.IndexOf("function notifyState(subscription)", StringComparison.Ordinal);
        var end = script.IndexOf("function recomputeState()", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "notifyState must exist before recomputeState.");
        var functionBody = script[start..end];

        Assert.Contains("subscription.fallbackWarned = false;", functionBody);
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
            if (File.Exists(Path.Join(directory.FullName, "OpenModulePlatform.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
    }

    private static string ReadRepositoryTextFile(params string[] relativePathSegments)
        => File.ReadAllText(GetRepositoryPath(relativePathSegments));

    private static SqlConnection CreateCommandOnlyConnection()
        => new(CommandOnlyConnectionString);

    private static string GetRepositoryPath(params string[] relativePathSegments)
    {
        var rootedSegment = relativePathSegments.FirstOrDefault(Path.IsPathRooted);
        if (rootedSegment is not null)
        {
            throw new ArgumentException("Repository test paths must be relative.", nameof(relativePathSegments));
        }

        var segments = new string[relativePathSegments.Length + 1];
        segments[0] = FindRepositoryRoot();
        Array.Copy(relativePathSegments, 0, segments, 1, relativePathSegments.Length);
        return Path.Join(segments);
    }

    private static void AssertParameter(SqlCommand cmd, string name, SqlDbType sqlDbType, object expectedValue)
    {
        var parameter = Assert.IsType<SqlParameter>(cmd.Parameters[name]);

        Assert.Equal(sqlDbType, parameter.SqlDbType);
        Assert.Equal(expectedValue, parameter.Value);
    }
}
