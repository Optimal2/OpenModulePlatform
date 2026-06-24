using OpenModulePlatform.Web.Shared.Notifications;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class SignalRTopBarNotificationStatePublisherTests
{
    [Fact]
    public async Task NotifyChangedAsync_PersistsUserTargetedOutboxEventAndSignalsUserGroup()
    {
        var pushEvents = new RecordingPushEventPublisher();
        var clientProxy = new RecordingClientProxy();
        var hubContext = new RecordingHubContext(clientProxy);
        var publisher = new SignalRTopBarNotificationStatePublisher(
            hubContext,
            pushEvents,
            NullLogger<SignalRTopBarNotificationStatePublisher>.Instance);

        await publisher.NotifyChangedAsync(42, CancellationToken.None);

        var pushEvent = Assert.Single(pushEvents.Events).Normalize();
        Assert.Equal(PushEventCategories.TopBarNotificationStateChanged, pushEvent.EventCategory);
        Assert.Equal(PushEventTargetTypes.User, pushEvent.TargetType);
        Assert.Equal(42, pushEvent.TargetUserId);
        Assert.Null(pushEvent.PayloadJson);

        Assert.Equal("omp-user:42", hubContext.ClientsImpl.GroupName);
        Assert.Equal(TopBarNotificationHub.StateChangedMethod, clientProxy.MethodName);
        Assert.Empty(clientProxy.Arguments);
    }

    [Fact]
    public async Task NotifyChangedAsync_WhenOutboxPublishFails_StillSignalsUserGroup()
    {
        var pushEvents = new RecordingPushEventPublisher
        {
            Exception = new InvalidOperationException("outbox unavailable")
        };
        var clientProxy = new RecordingClientProxy();
        var hubContext = new RecordingHubContext(clientProxy);
        var publisher = new SignalRTopBarNotificationStatePublisher(
            hubContext,
            pushEvents,
            NullLogger<SignalRTopBarNotificationStatePublisher>.Instance);

        await publisher.NotifyChangedAsync(42, CancellationToken.None);

        Assert.Equal("omp-user:42", hubContext.ClientsImpl.GroupName);
        Assert.Equal(TopBarNotificationHub.StateChangedMethod, clientProxy.MethodName);
    }

    private sealed class RecordingPushEventPublisher : IPushEventPublisher
    {
        public List<PushEvent> Events { get; } = [];

        public Exception? Exception { get; init; }

        public Task<long> PublishAsync(PushEvent pushEvent, CancellationToken ct)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            Events.Add(pushEvent);
            return Task.FromResult((long)Events.Count);
        }
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

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _clientProxy;

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
