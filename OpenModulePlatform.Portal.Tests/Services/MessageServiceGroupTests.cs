// File: OpenModulePlatform.Portal.Tests/Services/MessageServiceGroupTests.cs
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
/// Editing a message and changing a group's membership, against a real SQL
/// Server: the rules live in the SQL (who may edit, who may remove, who becomes
/// admin), so they are proven there rather than mocked. The push publisher is
/// recorded, not sent.
/// </summary>
public sealed class MessageServiceGroupTests : IClassFixture<MessageServiceTestFixture>
{
    private readonly MessageServiceTestFixture _fixture;

    public MessageServiceGroupTests(MessageServiceTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Only_the_sender_edits_and_the_edit_is_marked_and_pushed()
    {
        var (service, pushes) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil], "Edit test", CancellationToken.None);
        var messageId = await service.SendMessageAsync(anna, conversationId, "first draft", [], CancellationToken.None);
        pushes.Clear();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.EditMessageAsync(bertil, messageId, "not mine", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EditMessageAsync(anna, messageId, "   ", CancellationToken.None));

        var edited = await service.EditMessageAsync(anna, messageId, "  second draft ", CancellationToken.None);

        Assert.Equal(conversationId, edited);
        var row = Assert.Single(await service.GetMessagesAsync(bertil, conversationId, 10, null, CancellationToken.None));
        Assert.Equal("second draft", row.Content);
        Assert.NotNull(row.EditedAt);
        Assert.False(row.IsSystem);
        Assert.Equal(2, pushes.Count(p => p.PayloadJson!.Contains("\"action\":\"edited\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Only_the_creator_removes_others_and_the_group_hears_about_it()
    {
        var (service, pushes) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var cecilia = await _fixture.InsertUserAsync("Cecilia");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil, cecilia], "Remove test", CancellationToken.None);
        pushes.Clear();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RemoveParticipantAsync(bertil, conversationId, cecilia, CancellationToken.None));

        var removal = await service.RemoveParticipantAsync(anna, conversationId, cecilia, CancellationToken.None);

        Assert.False(removal.WasLeaving);
        Assert.Equal("Cecilia", removal.RemovedDisplayName);
        Assert.Null(removal.NewAdminUserId);
        Assert.Null(await service.GetConversationAsync(cecilia, conversationId, CancellationToken.None));
        var detail = await service.GetConversationAsync(bertil, conversationId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal([anna, bertil], detail!.Participants.Select(p => p.UserId));
        Assert.True(detail.Participants.Single(p => p.UserId == anna).IsAdmin);
        var line = Assert.Single(await service.GetMessagesAsync(bertil, conversationId, 10, null, CancellationToken.None));
        Assert.True(line.IsSystem);
        Assert.Equal("Anna", line.SenderDisplayName);
        Assert.Equal("removed Cecilia from the group", line.Content);
        // Everyone still in the group and the one who was removed get a push.
        Assert.Equal(3, pushes.Count(p => p.PayloadJson!.Contains("\"action\":\"membership\"", StringComparison.Ordinal)));

        // The removed member cannot be removed twice, and cannot remove anyone.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveParticipantAsync(anna, conversationId, cecilia, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RemoveParticipantAsync(cecilia, conversationId, bertil, CancellationToken.None));
    }

    [Fact]
    public async Task When_the_creator_leaves_the_earliest_joiner_becomes_admin()
    {
        var (service, _) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var cecilia = await _fixture.InsertUserAsync("Cecilia");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil, cecilia], "Leave test", CancellationToken.None);

        var removal = await service.RemoveParticipantAsync(anna, conversationId, anna, CancellationToken.None);

        Assert.True(removal.WasLeaving);
        Assert.Equal(bertil, removal.NewAdminUserId);
        Assert.Equal("Bertil", removal.NewAdminDisplayName);
        Assert.Null(await service.GetConversationAsync(anna, conversationId, CancellationToken.None));
        var detail = await service.GetConversationAsync(cecilia, conversationId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(bertil, detail!.CreatedByUserId);
        Assert.Equal([bertil, cecilia], detail.Participants.Select(p => p.UserId));
        Assert.True(detail.Participants.Single(p => p.UserId == bertil).IsAdmin);
        var lines = await service.GetMessagesAsync(cecilia, conversationId, 10, null, CancellationToken.None);
        Assert.Equal(["Anna left the group", "Bertil is now the group admin"], lines.Select(l => $"{l.SenderDisplayName} {l.Content}"));

        // The new admin may now remove; the last one out takes the conversation
        // with them: no rows remain, in any of the tables.
        await service.RemoveParticipantAsync(bertil, conversationId, cecilia, CancellationToken.None);
        var last = await service.RemoveParticipantAsync(bertil, conversationId, bertil, CancellationToken.None);
        Assert.True(last.ConversationDeleted);
        Assert.Null(last.NewAdminUserId);
        Assert.Equal(0, await _fixture.CountRowsAsync("omp.conversations", conversationId));
        Assert.Equal(0, await _fixture.CountRowsAsync("omp.messages", conversationId));
        Assert.Equal(0, await _fixture.CountRowsAsync("omp.conversation_participants", conversationId));
        Assert.Null(await service.GetConversationAsync(bertil, conversationId, CancellationToken.None));
    }

    [Fact]
    public async Task The_last_member_leaving_deletes_the_attachments_too()
    {
        var (service, _) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var conversationId = await service.CreateGroupConversationAsync(anna, [bertil], "Attachment test", CancellationToken.None);
        var file = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }), 0, 6, "Attachments", "note.txt")
        {
            Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(),
            ContentType = "text/plain"
        };
        var messageId = await service.SendMessageAsync(anna, conversationId, "with a file", [file], CancellationToken.None);
        Assert.Equal(1, await _fixture.CountAttachmentsAsync(messageId));

        await service.RemoveParticipantAsync(anna, conversationId, anna, CancellationToken.None);
        var last = await service.RemoveParticipantAsync(bertil, conversationId, bertil, CancellationToken.None);

        Assert.True(last.ConversationDeleted);
        Assert.Equal(0, await _fixture.CountAttachmentsAsync(messageId));
    }

    [Fact]
    public async Task A_direct_conversation_has_no_one_to_remove()
    {
        var (service, _) = _fixture.CreateService();
        var anna = await _fixture.InsertUserAsync("Anna");
        var bertil = await _fixture.InsertUserAsync("Bertil");
        var conversationId = await service.GetOrCreateDirectConversationAsync(anna, bertil, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveParticipantAsync(anna, conversationId, anna, CancellationToken.None));
    }
}

public sealed class MessageServiceTestFixture : IAsyncLifetime
{
    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("Messages");

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

    public async Task<int> CountRowsAsync(string table, long conversationId)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT COUNT(1) FROM {table} WHERE conversation_id = @id;", conn);
        cmd.Parameters.AddWithValue("@id", conversationId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> CountAttachmentsAsync(long messageId)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(1) FROM omp.message_attachments WHERE message_id = @id;", conn);
        cmd.Parameters.AddWithValue("@id", messageId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<int> InsertUserAsync(string displayName)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("INSERT INTO omp.users (display_name) VALUES (@name); SELECT CAST(SCOPE_IDENTITY() AS int);", conn);
        cmd.Parameters.AddWithValue("@name", displayName);
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
        catch (SqlException)
        {
            // Best-effort cleanup.
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
