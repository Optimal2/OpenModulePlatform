using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.EventPublisher;
using OpenModulePlatform.Web.Shared.Notifications;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class PushEventProducerTests
{
    [Fact]
    public async Task MigratingNotificationPublisher_UsesLegacySignalRWhenOutboxFlagIsOff()
    {
        var signalRClient = new RecordingClientProxy();
        var signalRContext = new RecordingHubContext(signalRClient);
        var pushPublisher = new RecordingPushEventPublisher();
        var publisher = CreateMigratingPublisher(
            signalRContext,
            pushPublisher,
            useOutbox: false);

        await publisher.NotifyChangedAsync(42, CancellationToken.None);

        Assert.Equal("omp-user:42", signalRContext.ClientsImpl.GroupName);
        Assert.Equal(TopBarNotificationHub.StateChangedMethod, signalRClient.MethodName);
        Assert.Empty(signalRClient.Arguments);
        Assert.Empty(pushPublisher.Events);
    }

    [Fact]
    public async Task MigratingNotificationPublisher_UsesOutboxWhenOutboxFlagIsOn()
    {
        var signalRClient = new RecordingClientProxy();
        var signalRContext = new RecordingHubContext(signalRClient);
        var pushPublisher = new RecordingPushEventPublisher();
        var publisher = CreateMigratingPublisher(
            signalRContext,
            pushPublisher,
            useOutbox: true);

        await publisher.NotifyChangedAsync(42, CancellationToken.None);

        var pushEvent = Assert.Single(pushPublisher.Events).Normalize();
        Assert.Equal(PushEventCategory.TopBarNotificationStateChanged.Value, pushEvent.EventCategory);
        Assert.Equal("user", pushEvent.TargetType);
        Assert.Equal(42, pushEvent.TargetUserId);
        Assert.Null(signalRContext.ClientsImpl.GroupName);
        Assert.Null(signalRClient.MethodName);
    }

    [Fact]
    public void MessageSentPushEvent_TargetsUserWithTypedCategoryAndPayload()
    {
        var pushEvent = MessageService.CreateMessageSentPushEvent(42, 123, 456).Normalize();

        Assert.Equal(PushEventCategory.TopBarMessageStateChanged.Value, pushEvent.EventCategory);
        Assert.Equal(42, pushEvent.TargetUserId);
        Assert.Equal("message:sent:456:user:42", pushEvent.DeduplicationKey);
        Assert.Equal("conversation:123", pushEvent.CorrelationKey);
        Assert.Contains("\"action\":\"sent\"", pushEvent.PayloadJson);
        Assert.Contains("\"conversationId\":123", pushEvent.PayloadJson);
        Assert.Contains("\"messageId\":456", pushEvent.PayloadJson);
    }

    [Fact]
    public void MessageReadPushEvent_TargetsReaderWithTypedCategory()
    {
        var pushEvent = MessageService.CreateMessageReadPushEvent(42, 123).Normalize();

        Assert.Equal(PushEventCategory.TopBarMessageStateChanged.Value, pushEvent.EventCategory);
        Assert.Equal(42, pushEvent.TargetUserId);
        Assert.Null(pushEvent.DeduplicationKey);
        Assert.Equal("conversation:123", pushEvent.CorrelationKey);
        Assert.Contains("\"action\":\"read\"", pushEvent.PayloadJson);
    }

    [Fact]
    public void BannerChangedPushEvent_TargetsBroadcastForGlobalBanner()
    {
        var events = BannerService.CreateBannerChangedPushEvents(
            "created",
            123,
            [new BannerTargetRequest(BannerService.TargetGlobal, null)]);

        var pushEvent = Assert.Single(events).Normalize();
        Assert.Equal(PushEventCategory.TopBarBannerStateChanged.Value, pushEvent.EventCategory);
        Assert.Equal("broadcast", pushEvent.TargetType);
        Assert.Equal("banner:123", pushEvent.CorrelationKey);
        Assert.Contains("\"action\":\"created\"", pushEvent.PayloadJson);
    }

    [Fact]
    public void BannerChangedPushEvent_TargetsAllRolesInOneEventForRoleBanner()
    {
        var events = BannerService.CreateBannerChangedPushEvents(
            "updated",
            123,
            [
                new BannerTargetRequest(BannerService.TargetRole, 7),
                new BannerTargetRequest(BannerService.TargetRole, 8)
            ]);

        var pushEvent = Assert.Single(events).Normalize();
        Assert.Equal(PushEventCategory.TopBarBannerStateChanged.Value, pushEvent.EventCategory);
        Assert.Equal("role", pushEvent.TargetType);
        Assert.Equal("""{"kind":"role","ids":["7","8"]}""", pushEvent.TargetJson);
        Assert.Contains("\"action\":\"updated\"", pushEvent.PayloadJson);
    }

    private static ITopBarNotificationStatePublisher CreateMigratingPublisher(
        IHubContext<TopBarNotificationHub> signalRContext,
        RecordingPushEventPublisher pushPublisher,
        bool useOutbox)
        => new MigratingTopBarNotificationStatePublisher(
            new SignalRTopBarNotificationStatePublisher(
                signalRContext,
                NullLogger<SignalRTopBarNotificationStatePublisher>.Instance),
            new OutboxTopBarNotificationStatePublisher(pushPublisher),
            new TestOptionsMonitor<PushEventProducerOptions>(new PushEventProducerOptions
            {
                UseOutboxForNotificationStateChanges = useOutbox
            }));

    private sealed class RecordingPushEventPublisher : IPushEventPublisher
    {
        private long _nextId;

        public List<PushEvent> Events { get; } = [];

        public Task<long> PublishAsync(PushEvent pushEvent, CancellationToken ct)
        {
            Events.Add(pushEvent);
            return Task.FromResult(++_nextId);
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;

        public TestOptionsMonitor(T value)
        {
            _value = value;
        }

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class RecordingHubContext : IHubContext<TopBarNotificationHub>
    {
        public RecordingHubContext(RecordingClientProxy clientProxy)
        {
            ClientsImpl = new RecordingHubClients(clientProxy);
        }

        public RecordingHubClients ClientsImpl { get; }

        public IHubClients Clients => ClientsImpl;

        public IGroupManager Groups { get; } = new NoOpGroupManager();
    }

    private sealed class RecordingHubClients : IHubClients
    {
        private readonly RecordingClientProxy _clientProxy;

        public RecordingHubClients(RecordingClientProxy clientProxy)
        {
            _clientProxy = clientProxy;
        }

        public string? GroupName { get; private set; }

        public IClientProxy All => _clientProxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _clientProxy;

        public IClientProxy Client(string connectionId) => _clientProxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _clientProxy;

        public IClientProxy Group(string groupName)
        {
            GroupName = groupName;
            return _clientProxy;
        }

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> groupNameExcludedConnectionIds) => _clientProxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _clientProxy;

        public IClientProxy User(string userId) => _clientProxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => _clientProxy;
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        public string? MethodName { get; private set; }

        public object?[] Arguments { get; private set; } = [];

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            MethodName = method;
            Arguments = args;
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
