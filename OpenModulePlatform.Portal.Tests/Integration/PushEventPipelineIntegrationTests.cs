using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OpenModulePlatform.EventPublisher;
using OpenModulePlatform.Web.Shared.Notifications;
using System.Collections.Concurrent;
using System.Text.Json;

namespace OpenModulePlatform.Portal.Tests.Integration;

public sealed class PushEventPipelineIntegrationTests : IClassFixture<PushEventPipelineTestFixture>
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
