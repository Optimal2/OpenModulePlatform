// File: OpenModulePlatform.Portal.Tests/Services/MessageServiceQueryCountTests.cs
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.EventPublisher;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Services;
using Xunit;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// Sending a message used to run one unread-count query per recipient inside the
/// sender's request, so the request grew with the size of the group. These tests
/// count the SQL commands the service itself executes, against a real SQL Server,
/// and prove the number no longer depends on how many people receive the message
/// while every recipient still gets exactly one push, in user order, with the
/// right unread count.
/// </summary>
public sealed class MessageServiceQueryCountTests : IClassFixture<MessageServiceQueryCountTestFixture>
{
    private readonly MessageServiceQueryCountTestFixture _fixture;

    public MessageServiceQueryCountTests(MessageServiceQueryCountTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SendMessage_runs_the_same_bounded_number_of_commands_for_a_small_and_a_large_group()
    {
        var (service, pushes) = _fixture.CreateService();
        var sender = await _fixture.InsertUserAsync("Sender");
        var smallGroup = await InsertUsersAsync("Small", 2);
        var largeGroup = await InsertUsersAsync("Large", 12);
        var smallConversation = await service.CreateGroupConversationAsync(sender, smallGroup, "Small group", CancellationToken.None);
        var largeConversation = await service.CreateGroupConversationAsync(sender, largeGroup, "Large group", CancellationToken.None);

        // Warm the configuration cache so both measured sends see the same cache state.
        await service.SendMessageAsync(sender, smallConversation, "warm-up", [], CancellationToken.None);
        pushes.Clear();

        var (smallMessageId, smallCommands) = await CountCommandsAsync(
            () => service.SendMessageAsync(sender, smallConversation, "hello small", [], CancellationToken.None));
        var smallPushes = pushes.ToArray();
        pushes.Clear();

        var (largeMessageId, largeCommands) = await CountCommandsAsync(
            () => service.SendMessageAsync(sender, largeConversation, "hello large", [], CancellationToken.None));
        var largePushes = pushes.ToArray();

        Assert.True(smallCommands > 0, "The SqlClient diagnostic listener recorded no commands; the count would prove nothing.");
        Assert.Equal(smallCommands, largeCommands);
        Assert.InRange(largeCommands, 1, MaxCommandsPerSend);

        // Two messages are unread for the small group's recipients (warm-up and the
        // measured one), one for the large group's; the sender has nothing unread.
        AssertSentPushes(smallPushes, smallMessageId, sender, smallGroup, expectedRecipientUnread: 2);
        AssertSentPushes(largePushes, largeMessageId, sender, largeGroup, expectedRecipientUnread: 1);
    }

    // With the configuration cached a send runs 4 fixed commands plus the one
    // batched unread count. Before batching it was 4 plus one per recipient (7 for
    // the small group here, 17 for the large one). The headroom keeps the test about
    // growth, not about the exact statement list.
    private const int MaxCommandsPerSend = 10;

    private async Task<int[]> InsertUsersAsync(string prefix, int count)
    {
        var ids = new int[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = await _fixture.InsertUserAsync($"{prefix} {i}");
        }

        return ids;
    }

    private static void AssertSentPushes(
        PushEvent[] pushes,
        long messageId,
        int sender,
        int[] recipients,
        int expectedRecipientUnread)
    {
        var expectedUsers = recipients.Append(sender).Order().ToArray();
        Assert.Equal(expectedUsers, pushes.Select(push => push.TargetUserId!.Value).ToArray());

        foreach (var push in pushes)
        {
            using var payload = JsonDocument.Parse(push.PayloadJson!);
            Assert.Equal("sent", payload.RootElement.GetProperty("action").GetString());
            Assert.Equal(messageId, payload.RootElement.GetProperty("messageId").GetInt64());
            var expectedUnread = push.TargetUserId == sender ? 0 : expectedRecipientUnread;
            Assert.Equal(expectedUnread, payload.RootElement.GetProperty("unreadMessageCount").GetInt32());
            Assert.Equal($"message:sent:{messageId}:user:{push.TargetUserId}", push.DeduplicationKey);
        }
    }

    private static async Task<(T Result, int Commands)> CountCommandsAsync<T>(Func<Task<T>> action)
    {
        using var counter = new SqlCommandCounter();
        var result = await counter.RunAsync(action);
        return (result, counter.Count);
    }

    /// <summary>
    /// Counts SqlClient command executions started by one async flow. SqlClient raises
    /// WriteCommandBefore synchronously on the executing flow, so an AsyncLocal marker
    /// keeps commands from tests running in parallel out of the count.
    /// </summary>
    private sealed class SqlCommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private const string ListenerName = "SqlClientDiagnosticListener";
        private const string CommandBeforeEvent = "Microsoft.Data.SqlClient.WriteCommandBefore";

        private static readonly AsyncLocal<SqlCommandCounter?> Current = new();
        private readonly List<IDisposable> _subscriptions = [];
        private int _count;

        public SqlCommandCounter()
        {
            _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        }

        public int Count => Volatile.Read(ref _count);

        public async Task<T> RunAsync<T>(Func<Task<T>> action)
        {
            Current.Value = this;
            try
            {
                return await action();
            }
            finally
            {
                Current.Value = null;
            }
        }

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == ListenerName)
            {
                lock (_subscriptions)
                {
                    _subscriptions.Add(value.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == CommandBeforeEvent && ReferenceEquals(Current.Value, this))
            {
                Interlocked.Increment(ref _count);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            lock (_subscriptions)
            {
                foreach (var subscription in _subscriptions)
                {
                    subscription.Dispose();
                }

                _subscriptions.Clear();
            }
        }
    }
}

public sealed class MessageServiceQueryCountTestFixture : IAsyncLifetime
{
    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("MessagesQueryCount");

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public (MessageService Service, List<PushEvent> Pushes) CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:OmpDb"] = ConnectionString })
            .Build();
        var db = new SqlConnectionFactory(configuration);
        var pushes = new List<PushEvent>();
        var service = new MessageService(
            db,
            new OmpConfigurationService(db, new MemoryCache(new MemoryCacheOptions()), NullLogger<OmpConfigurationService>.Instance),
            new RecordingPushEventPublisher(pushes),
            NullLogger<MessageService>.Instance);
        return (service, pushes);
    }

    public async Task InitializeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            master.ConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");
        await CoreSetupScript.ApplyAsync(ConnectionString);
    }

    public async Task<int> InsertUserAsync(string displayName)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("INSERT INTO omp.users (display_name) VALUES (@name); SELECT CAST(SCOPE_IDENTITY() AS int);", conn);
        cmd.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 200).Value = displayName;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task DisposeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(master.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];", conn);
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqlException ex)
        {
            // A failed drop must not fail the run, but it must not be silent either:
            // the shared log is what CI turns into a warning about the leaked database.
            OmpTestCleanupLog.RecordFailure(
                nameof(MessageServiceQueryCountTests),
                $"Could not drop test database '{DatabaseName}': {ex.Message}");
        }
    }

    private sealed class RecordingPushEventPublisher(List<PushEvent> pushes) : IPushEventPublisher
    {
        public Task<long> PublishAsync(PushEvent pushEvent, CancellationToken ct)
        {
            pushes.Add(pushEvent);
            return Task.FromResult((long)pushes.Count);
        }
    }
}
