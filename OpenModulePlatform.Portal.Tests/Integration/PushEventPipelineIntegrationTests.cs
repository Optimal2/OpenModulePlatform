using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OpenModulePlatform.EventPublisher;
using OpenModulePlatform.Web.Shared.Notifications;
using System.Collections.Concurrent;
using System.Text.Json;

namespace OpenModulePlatform.Portal.Tests.Integration;

[Collection(PushEventPipelineTestCollection.Name)]
public sealed class PushEventPipelineIntegrationTests
{
    private readonly PushEventPipelineTestFixture _fixture;

    public PushEventPipelineIntegrationTests(PushEventPipelineTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task UserTargetedNotificationPush_DeliversPushEventAndLegacyStateChanged()
    {
        await _fixture.CleanOutboxAsync();

        var receivedEvents = await ConnectAndCollectEventsAsync(
            PushEventPipelineTestFixture.TestUserId,
            async publisher =>
            {
                var pushEvent = PushEvent.ForUser(
                    PushEventPipelineTestFixture.TestUserId,
                    PushEventCategory.TopBarNotificationStateChanged,
                    """{"unreadCount":7,"source":"test"}""",
                    deduplicationKey: $"integration-test:notification:{Guid.NewGuid():N}");

                await publisher.PublishAsync(pushEvent, CancellationToken.None);
                return (pushEvent.EventCategory, ExpectLegacyStateChanged: true);
            });

        AssertPushEventReceived(
            receivedEvents,
            PushEventCategory.TopBarNotificationStateChanged.Value,
            "user",
            PushEventPipelineTestFixture.TestUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            expectedLegacyStateChanged: true);
    }

    [Fact]
    public async Task UserTargetedMessagePush_DeliversPushEventAndLegacyStateChanged()
    {
        await _fixture.CleanOutboxAsync();

        var receivedEvents = await ConnectAndCollectEventsAsync(
            PushEventPipelineTestFixture.TestUserId,
            async publisher =>
            {
                var pushEvent = PushEvent.ForUser(
                    PushEventPipelineTestFixture.TestUserId,
                    PushEventCategory.TopBarMessageStateChanged,
                    """{"conversationId":123,"messageId":456,"action":"sent"}""",
                    deduplicationKey: $"integration-test:message:{Guid.NewGuid():N}");

                await publisher.PublishAsync(pushEvent, CancellationToken.None);
                return (pushEvent.EventCategory, ExpectLegacyStateChanged: true);
            });

        AssertPushEventReceived(
            receivedEvents,
            PushEventCategory.TopBarMessageStateChanged.Value,
            "user",
            PushEventPipelineTestFixture.TestUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            expectedLegacyStateChanged: true);
    }

    [Fact]
    public async Task BroadcastPush_DeliversPushEventToConnectedClient()
    {
        await _fixture.CleanOutboxAsync();

        var receivedEvents = await ConnectAndCollectEventsAsync(
            PushEventPipelineTestFixture.TestUserId,
            async publisher =>
            {
                var pushEvent = PushEvent.ForBroadcast(
                    PushEventCategory.TopBarBannerStateChanged,
                    """{"bannerId":99,"action":"updated"}""",
                    deduplicationKey: $"integration-test:broadcast:{Guid.NewGuid():N}");

                await publisher.PublishAsync(pushEvent, CancellationToken.None);
                return (pushEvent.EventCategory, ExpectLegacyStateChanged: false);
            });

        AssertPushEventReceived(
            receivedEvents,
            PushEventCategory.TopBarBannerStateChanged.Value,
            "broadcast",
            expectedTargetValue: null,
            expectedLegacyStateChanged: false);
    }

    // Regression test for the module-targeted push investigation (2026-08-22):
    // before the fix, a client connected to the Portal hub only joined the
    // module group of the Portal's own configured ModuleKey (omp_portal), so a
    // push targeted at a different module key was dispatched to an empty
    // SignalR group, marked 'dispatched' in the outbox, and never delivered
    // (proven by running this scenario against the pre-fix dispatcher, where
    // zero events arrived and the row was marked dispatched). Module targets
    // are now delivered to the authenticated group and scoped by the payload
    // "module" discriminator, which module clients filter on. No polling
    // fallback exists in this harness -- only the live SignalR channel is
    // observed.
    [Fact]
    public async Task ModuleTargetedPush_ForForeignModule_IsDeliveredForClientSideFiltering()
    {
        await _fixture.CleanOutboxAsync();

        var receivedEvents = await ConnectAndCollectModuleEventsAsync(
            PushEventPipelineTestFixture.TestUserId,
            "earkiv_checker",
            waitForDelivery: true);

        Assert.True(
            receivedEvents.PushEvents.Length > 0,
            $"Module push must reach authenticated clients for payload filtering. Outbox rows after wait: {FormatOutboxStatuses(receivedEvents.OutboxStatuses)}");
        var pushEvent = Assert.Single(receivedEvents.PushEvents);
        Assert.Equal(PushEventCategory.ModuleStateChanged.Value, pushEvent.GetProperty("category").GetString());
        Assert.Equal("module", pushEvent.GetProperty("targetKind").GetString());
        Assert.Equal("earkiv_checker", pushEvent.GetProperty("targetValue").GetString());
        Assert.Equal("earkiv_checker", pushEvent.GetProperty("payload").GetProperty("module").GetString());

        var row = Assert.Single(receivedEvents.OutboxStatuses.ToArray());
        Assert.Equal("dispatched", row.Status);
    }

    // Sabotage test: a foreign/unknown target kind (for example written by a
    // future or out-of-band sender) must not be swallowed. The dispatcher has
    // no groups for it, so the dispatch must fail loudly and the outbox row
    // must record the error instead of being marked dispatched.
    [Fact]
    public async Task UnknownTargetKind_IsNotSwallowed_RowRecordsDispatchError()
    {
        // Touch the test server so the Portal host (and with it the push event
        // dispatcher hosted service) is actually started; WebApplicationFactory
        // creates the host lazily.
        _ = _fixture.Factory.Server.BaseAddress;

        await _fixture.CleanOutboxAsync();
        await InsertRawOutboxRowAsync("pigeon", """{"kind":"pigeon","ids":["x"]}""");

        var deadline = DateTime.UtcNow.AddSeconds(20);
        PushEventPipelineTestFixture.OutboxRowStatus? row = null;
        while (DateTime.UtcNow < deadline)
        {
            row = Assert.Single((await _fixture.GetOutboxStatusesAsync()).ToArray());
            if (row.ErrorMessage is not null || row.Status is "failed" or "dead-lettered")
            {
                break;
            }

            await Task.Delay(250);
        }

        if (row is null)
        {
            throw new Xunit.Sdk.XunitException(
                "The sabotage outbox row was never observed before the deadline.");
        }

        Assert.NotEqual("dispatched", row.Status);
        Assert.True(
            row.RetryCount > 0,
            $"Row must show a recorded dispatch attempt failure. Outbox rows: {FormatOutboxStatuses([row])}");
        Assert.NotNull(row.ErrorMessage);
        Assert.Contains("no SignalR target groups", row.ErrorMessage);
    }

    private async Task InsertRawOutboxRowAsync(string targetType, string targetJson)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
            """
            INSERT INTO omp.push_event_outbox (event_category, target_type, target_json, payload_json, deduplication_key)
            VALUES (N'module.state-changed', @target_type, @target_json, N'{"module":"sabotage"}', @dedup_key);
            """,
            conn);
        cmd.Parameters.Add("@target_type", System.Data.SqlDbType.NVarChar, 40).Value = targetType;
        cmd.Parameters.Add("@target_json", System.Data.SqlDbType.NVarChar, 2048).Value = targetJson;
        cmd.Parameters.Add("@dedup_key", System.Data.SqlDbType.NVarChar, 200).Value = $"integration-test:sabotage:{Guid.NewGuid():N}";
        await cmd.ExecuteNonQueryAsync();
    }

    // Control: the same module-targeted push DOES arrive when the target module
    // key happens to equal the hosting app's own ModuleKey, which is the only
    // reason the module-group path ever appeared to work.
    [Fact]
    public async Task ModuleTargetedPush_ForHostOwnModuleKey_IsDelivered()
    {
        await _fixture.CleanOutboxAsync();

        var receivedEvents = await ConnectAndCollectModuleEventsAsync(
            PushEventPipelineTestFixture.TestUserId,
            "omp_portal",
            waitForDelivery: true);

        Assert.True(
            receivedEvents.PushEvents.Length > 0,
            $"Outbox rows after wait: {FormatOutboxStatuses(receivedEvents.OutboxStatuses)}");
        var pushEvent = Assert.Single(receivedEvents.PushEvents);
        Assert.Equal(PushEventCategory.ModuleStateChanged.Value, pushEvent.GetProperty("category").GetString());
        Assert.Equal("module", pushEvent.GetProperty("targetKind").GetString());
        Assert.Equal("omp_portal", pushEvent.GetProperty("targetValue").GetString());
    }

    private async Task<CollectedEvents> ConnectAndCollectModuleEventsAsync(
        int userId,
        string moduleKey,
        bool waitForDelivery)
    {
        var pushEvents = new ConcurrentBag<JsonElement>();
        var legacyStateChangedEvents = new ConcurrentBag<JsonElement>();

        var hubUrl = new Uri(_fixture.Factory.Server.BaseAddress, TopBarNotificationHub.Path);
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => _fixture.Factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            })
            .Build();

        connection.On<JsonElement>(TopBarNotificationHub.PushEventMethod, envelope => pushEvents.Add(envelope));
        connection.On<JsonElement>(TopBarNotificationHub.StateChangedMethod, envelope => legacyStateChangedEvents.Add(envelope));

        await connection.StartAsync();
        try
        {
            await using var scope = _fixture.Factory.Services.CreateAsyncScope();
            var publisher = scope.ServiceProvider.GetRequiredService<IPushEventPublisher>();

            var pushEvent = new PushEvent(
                PushEventCategory.ModuleStateChanged,
                PushTarget.ForModule(moduleKey),
                $$"""{"module":"{{moduleKey}}","reason":"measurement"}""",
                DeduplicationKey: $"integration-test:module:{Guid.NewGuid():N}");

            await publisher.PublishAsync(pushEvent, CancellationToken.None);

            // Delivery arrives within a second or two when it works; the negative
            // case must wait the full window to prove nothing ever arrives.
            var deadline = DateTime.UtcNow.AddSeconds(waitForDelivery ? 15 : 8);
            while (DateTime.UtcNow < deadline)
            {
                if (waitForDelivery && !pushEvents.IsEmpty)
                {
                    break;
                }

                await Task.Delay(100);
            }
        }
        finally
        {
            await connection.DisposeAsync();
        }

        var outboxStatuses = await _fixture.GetOutboxStatusesAsync();
        return new CollectedEvents(pushEvents.ToArray(), legacyStateChangedEvents.ToArray(), outboxStatuses);
    }

    private async Task<CollectedEvents> ConnectAndCollectEventsAsync(
        int userId,
        Func<IPushEventPublisher, Task<(string Category, bool ExpectLegacyStateChanged)>> publish)
    {
        var pushEvents = new ConcurrentBag<JsonElement>();
        var legacyStateChangedEvents = new ConcurrentBag<JsonElement>();

        var hubUrl = new Uri(_fixture.Factory.Server.BaseAddress, TopBarNotificationHub.Path);
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.HttpMessageHandlerFactory = _ => _fixture.Factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            })
            .Build();

        connection.On<JsonElement>(TopBarNotificationHub.PushEventMethod, envelope => pushEvents.Add(envelope));
        connection.On<JsonElement>(TopBarNotificationHub.StateChangedMethod, envelope => legacyStateChangedEvents.Add(envelope));

        await connection.StartAsync();
        try
        {
            await using var scope = _fixture.Factory.Services.CreateAsyncScope();
            var publisher = scope.ServiceProvider.GetRequiredService<IPushEventPublisher>();
            var publishInfo = await publish(publisher);

            await WaitForEventsAsync(pushEvents, legacyStateChangedEvents, publishInfo.ExpectLegacyStateChanged);
        }
        finally
        {
            await connection.DisposeAsync();
        }

        var outboxStatuses = await _fixture.GetOutboxStatusesAsync();
        return new CollectedEvents(pushEvents.ToArray(), legacyStateChangedEvents.ToArray(), outboxStatuses);
    }

    private static async Task WaitForEventsAsync(
        ConcurrentBag<JsonElement> pushEvents,
        ConcurrentBag<JsonElement> legacyStateChangedEvents,
        bool expectLegacyStateChanged)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (!pushEvents.IsEmpty && (!expectLegacyStateChanged || !legacyStateChangedEvents.IsEmpty))
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    private static void AssertPushEventReceived(
        CollectedEvents events,
        string expectedCategory,
        string expectedTargetKind,
        string? expectedTargetValue,
        bool expectedLegacyStateChanged)
    {
        var diagnosticMessage = $"Outbox rows after wait: {FormatOutboxStatuses(events.OutboxStatuses)}";

        Assert.True(events.PushEvents.Length > 0, diagnosticMessage);
        var pushEvent = Assert.Single(events.PushEvents);
        Assert.Equal(expectedCategory, pushEvent.GetProperty("category").GetString());
        Assert.Equal(expectedTargetKind, pushEvent.GetProperty("targetKind").GetString());

        if (expectedTargetValue is null)
        {
            Assert.True(
                !pushEvent.TryGetProperty("targetValue", out var targetValue)
                || string.IsNullOrEmpty(targetValue.GetString()));
        }
        else
        {
            Assert.Equal(expectedTargetValue, pushEvent.GetProperty("targetValue").GetString());
        }

        Assert.True(pushEvent.TryGetProperty("eventId", out var eventId));
        Assert.True(eventId.GetInt64() > 0);

        Assert.True(pushEvent.TryGetProperty("deduplicationKey", out _));

        if (expectedLegacyStateChanged)
        {
            var legacyEvent = Assert.Single(events.LegacyStateChangedEvents);
            Assert.Equal(expectedCategory, legacyEvent.GetProperty("category").GetString());
        }
        else
        {
            Assert.Empty(events.LegacyStateChangedEvents);
        }
    }

    private static string FormatOutboxStatuses(IReadOnlyList<PushEventPipelineTestFixture.OutboxRowStatus> statuses)
        => statuses.Count == 0
            ? "none"
            : string.Join(
                "; ",
                statuses.Select(s =>
                    $"#{s.PushEventId} {s.EventCategory}/{s.TargetType} status={s.Status} retries={s.RetryCount}"));

    private sealed record CollectedEvents(
        JsonElement[] PushEvents,
        JsonElement[] LegacyStateChangedEvents,
        IReadOnlyList<PushEventPipelineTestFixture.OutboxRowStatus> OutboxStatuses);
}
